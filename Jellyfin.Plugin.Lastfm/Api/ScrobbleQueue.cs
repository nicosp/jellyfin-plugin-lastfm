namespace Jellyfin.Plugin.Lastfm.Api
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using Microsoft.Extensions.Logging;
    using Models;

    /// <summary>
    /// Scrobbles waiting to be (re)submitted, persisted to disk so they survive restarts.
    /// </summary>
    /// <remarks>
    /// Implements the caching guidance in https://www.last.fm/api/scrobbling: scrobbles that
    /// fail for a temporary reason are kept and resubmitted later, oldest first, before any
    /// new scrobble for the same user.
    /// </remarks>
    public sealed class ScrobbleQueue
    {
        public const string FileName = "Jellyfin.Plugin.Lastfm.scrobble-queue.json";

        /// <summary>Upper bound on stored scrobbles; the oldest are dropped beyond it.</summary>
        public const int MaxEntries = 10000;

        private readonly object _lock = new();
        private readonly Func<string> _filePathProvider;
        private readonly ILogger _logger;
        private List<QueuedScrobble> _items;

        // Users whose session key Last.fm rejected (error 9), with a hash of that key.
        // Their scrobbles are held until the key changes, i.e. the account is re-linked.
        private Dictionary<Guid, string> _invalidSessionKeys;

        /// <param name="logger">Logger.</param>
        /// <param name="filePathProvider">
        /// Returns the file to persist to, or null to keep the queue in memory only.
        /// Resolved lazily because the plugin's paths are not known at construction time.
        /// </param>
        public ScrobbleQueue(ILogger logger, Func<string> filePathProvider)
        {
            _logger = logger;
            _filePathProvider = filePathProvider;
        }

        /// <summary>Default location: next to the plugin's XML configuration, which survives plugin upgrades.</summary>
        public static string DefaultFilePath()
        {
            var configFile = Plugin.Instance?.ConfigurationFilePath;
            var directory = string.IsNullOrEmpty(configFile) ? null : Path.GetDirectoryName(configFile);
            return string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, FileName);
        }

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    EnsureLoaded();
                    return _items.Count;
                }
            }
        }

        public bool HasPending(Guid userId)
        {
            lock (_lock)
            {
                EnsureLoaded();
                return _items.Any(i => i.UserId == userId);
            }
        }

        public IReadOnlyList<Guid> UsersWithPending()
        {
            lock (_lock)
            {
                EnsureLoaded();
                return _items.Select(i => i.UserId).Distinct().ToList();
            }
        }

        public void Enqueue(QueuedScrobble scrobble)
        {
            lock (_lock)
            {
                EnsureLoaded();
                _items.Add(scrobble);

                var overflow = _items.Count - MaxEntries;
                if (overflow > 0)
                {
                    _items.RemoveRange(0, overflow);
                    _logger.LogWarning("Scrobble queue is full; dropped the {Count} oldest scrobble(s)", overflow);
                }

                Save();
            }
        }

        /// <summary>The oldest pending scrobbles for a user, at most <paramref name="max"/>.</summary>
        public IReadOnlyList<QueuedScrobble> PeekBatch(Guid userId, int max)
        {
            lock (_lock)
            {
                EnsureLoaded();
                return _items.Where(i => i.UserId == userId).Take(max).ToList();
            }
        }

        public void Remove(IEnumerable<QueuedScrobble> scrobbles)
        {
            lock (_lock)
            {
                EnsureLoaded();
                var toRemove = new HashSet<QueuedScrobble>(scrobbles, ReferenceEqualityComparer.Instance);
                if (_items.RemoveAll(toRemove.Contains) > 0)
                {
                    Save();
                }
            }
        }

        /// <summary>Holds a user's scrobbles until their session key changes.</summary>
        public void MarkInvalidSession(Guid userId, string sessionKey)
        {
            lock (_lock)
            {
                EnsureLoaded();
                _invalidSessionKeys[userId] = HashSessionKey(sessionKey);
                Save();
            }
        }

        /// <summary>
        /// True when the user's current session key is one Last.fm already rejected.
        /// Once the key changes the hold is lifted.
        /// </summary>
        public bool IsAwaitingReauthentication(Guid userId, string currentSessionKey)
        {
            lock (_lock)
            {
                EnsureLoaded();
                if (!_invalidSessionKeys.TryGetValue(userId, out var rejected))
                {
                    return false;
                }

                if (rejected == HashSessionKey(currentSessionKey))
                {
                    return true;
                }

                _invalidSessionKeys.Remove(userId);
                Save();
                return false;
            }
        }

        // Only a hash is stored so the queue file does not duplicate credentials.
        private static string HashSessionKey(string sessionKey) =>
            Utils.Helpers.CreateMd5Hash(sessionKey ?? string.Empty);

        private void EnsureLoaded()
        {
            if (_items != null)
            {
                return;
            }

            _items = new List<QueuedScrobble>();
            _invalidSessionKeys = new Dictionary<Guid, string>();

            var path = _filePathProvider();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return;
            }

            try
            {
                var state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(path));
                if (state?.Scrobbles != null)
                {
                    _items.AddRange(state.Scrobbles.Where(s => s != null));
                }

                if (state?.InvalidSessionKeys != null)
                {
                    _invalidSessionKeys = new Dictionary<Guid, string>(state.InvalidSessionKeys);
                }

                if (_items.Count > 0)
                {
                    _logger.LogInformation("Loaded {Count} pending scrobble(s) from {Path}", _items.Count, path);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Keep the unreadable file for inspection instead of overwriting it on the next save.
                var backup = path + ".corrupt";
                _logger.LogError(ex, "Could not read the scrobble queue at {Path}; moving it to {Backup} and starting empty", path, backup);
                try
                {
                    File.Move(path, backup, overwrite: true);
                }
                catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
                {
                    _logger.LogError(moveEx, "Could not move the unreadable scrobble queue aside");
                }
            }
        }

        private void Save()
        {
            var path = _filePathProvider();
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                // Write to a temporary file and swap it in, so a crash mid-write cannot corrupt the queue.
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(new PersistedState
                {
                    Scrobbles = _items,
                    InvalidSessionKeys = _invalidSessionKeys
                }));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Could not save the scrobble queue to {Path}; pending scrobbles are kept in memory only", path);
            }
        }

        private sealed class PersistedState
        {
            public List<QueuedScrobble> Scrobbles { get; set; }

            public Dictionary<Guid, string> InvalidSessionKeys { get; set; }
        }
    }
}
