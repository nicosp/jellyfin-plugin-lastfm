namespace Jellyfin.Plugin.Lastfm.Api
{
    using MediaBrowser.Controller.Entities.Audio;
    using Models;
    using Models.Requests;
    using Models.Responses;
    using Resources;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Microsoft.Extensions.Caching.Memory;
    using System.Linq;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Utils;
    using Microsoft.Extensions.Logging;

    public class LastfmApiClient : BaseLastfmApiClient
    {
        private readonly ILogger _logger;

        // Last.fm ignores scrobbles with a timestamp more than 14 days old (ignored code 3).
        internal static readonly TimeSpan MaxScrobbleAge = TimeSpan.FromDays(14);

        // Retain play-through identities as long as ServerEntryPoint retains their start times.
        private static readonly TimeSpan DuplicateScrobbleTTL = TimeSpan.FromHours(24);
        private readonly MemoryCache _scrobbleCache = new(new MemoryCacheOptions());
        private readonly object _scrobbleLock = new();

        private readonly ScrobbleQueue _queue;
        private readonly SemaphoreSlim _flushLock = new(1, 1);

        public LastfmApiClient(IHttpClientFactory httpClientFactory, ILogger logger) : base(httpClientFactory, logger)
        {
            _logger = logger;
            _queue = new ScrobbleQueue(logger, ScrobbleQueue.DefaultFilePath);
        }

        /// <summary>Scrobbles waiting to be resubmitted.</summary>
        public ScrobbleQueue Queue => _queue;

        private enum SubmitOutcome
        {
            /// <summary>Last.fm processed the request (accepted or ignored).</summary>
            Ok,
            /// <summary>Temporary failure: keep the scrobble and retry later.</summary>
            Retry,
            /// <summary>The session key was rejected (error 9): hold until the account is re-linked.</summary>
            InvalidSession,
            /// <summary>Permanent rejection: retrying unchanged would fail the same way.</summary>
            Rejected
        }

        /// <summary>
        /// Classifies a response per https://www.last.fm/api/scrobbling: retry errors 11 and 16,
        /// re-authenticate on 9, and do not retry anything else. Rate limiting (29 / HTTP 429)
        /// and missing or unreadable responses are also treated as temporary.
        /// </summary>
        private static SubmitOutcome Classify(BaseResponse response)
        {
            if (response == null)
            {
                return SubmitOutcome.Retry;
            }

            if (!response.IsError())
            {
                return SubmitOutcome.Ok;
            }

            return response.ErrorCode switch
            {
                9 => SubmitOutcome.InvalidSession,
                11 or 16 or LastfmRateLimit.RateLimitErrorCode => SubmitOutcome.Retry,
                _ => SubmitOutcome.Rejected
            };
        }

        private static bool IsTransportFailure(Exception ex) =>
            ex is HttpRequestException or TaskCanceledException or IOException;



        /// <param name="apiHost">Host to authenticate against (e.g. libre.fm). Falls back to the configured host when empty.</param>
        public async Task<MobileSessionResponse> RequestSession(string username, string password, string apiHost = null)
        {
            var host = GetApiEndpointHost(apiHost);

            //Build request object
            var request = new MobileSessionRequest
            {
                Username = username,
                Password = password,

                ApiKey = Strings.Keys.LastfmApiKey,
                Method = Strings.Methods.GetMobileSession,
                Secure = true
            };

            var response = await Post<MobileSessionRequest, MobileSessionResponse>(request, host);

            if (ShouldRetryWithLegacyAuthToken(response, host))
            {
                _logger.LogInformation("Retrying mobile session auth with legacy authToken flow for host={Host}", host);

                request.Password = null;
                request.AuthToken = BuildLegacyAuthToken(username, password);
                response = await Post<MobileSessionRequest, MobileSessionResponse>(request, host);
            }


            return response;
        }

        private static string BuildLegacyAuthToken(string username, string password)
        {
            var passwordHash = Helpers.CreateMd5Hash(password).ToLowerInvariant();
            return Helpers.CreateMd5Hash(username + passwordHash).ToLowerInvariant();
        }

        private static bool ShouldRetryWithLegacyAuthToken(MobileSessionResponse response, string host)
        {
            if (response == null || !response.IsError())
            {
                return false;
            }

            var isLibreHost = !string.IsNullOrWhiteSpace(host) && host.Contains("libre.fm", StringComparison.OrdinalIgnoreCase);
            if (!isLibreHost)
            {
                return false;
            }

            if (response.ErrorCode == 6)
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(response.Message)
                && response.Message.Contains("missing a required parameter", StringComparison.OrdinalIgnoreCase);
        }

        public async Task Scrobble(Audio item, LastfmUser user, DateTime playbackStartTime)
        {
            var artist = item.Artists.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(artist))
            {
                _logger.LogWarning("Skipping scrobble for '{0}': no artist tagged", item.Name);
                return;
            }

            // Last.fm: send cached scrobbles before new ones, in order. Done before the
            // duplicate check so repeated stop events for a queued play also retry the queue.
            if (_queue.HasPending(user.MediaBrowserUserId))
            {
                await FlushQueueAsync(CancellationToken.None).ConfigureAwait(false);
            }

            var cacheKey = $"{user.Username}:{item.Id}:{playbackStartTime.Ticks}";
            if (CheckAndUpdateScrobbleCache(cacheKey))
                return;

            // API docs -> https://www.last.fm/api/show/track.scrobble
            // Timestamp must be the time the track started playing (UTC), not the
            // time the scrobble is submitted.
            var request = new ScrobbleRequest
            {
                Track = item.Name,
                Artist = artist,
                Timestamp = Helpers.ToTimestamp(playbackStartTime),

                ApiKey = Strings.Keys.LastfmApiKey,
                Method = Strings.Methods.Scrobble,
                SessionKey = user.SessionKey,
                Secure = true
            };

            if (!string.IsNullOrWhiteSpace(item.Album))
            {
                request.Album = item.Album;
            }
            if (item.ProviderIds.ContainsKey("MusicBrainzTrack"))
            {
                request.MbId = item.ProviderIds["MusicBrainzTrack"];
            }
            var albumArtist = item.AlbumArtists.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(albumArtist) && albumArtist != request.Artist)
            {
                request.AlbumArtist = albumArtist;
            }
            if (item.RunTimeTicks is > 0)
            {
                request.Duration = (int)TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalSeconds;
            }
            request.TrackNumber = item.IndexNumber;

            // Keep the duplicate guard when the scrobble was sent or queued; release it otherwise
            // so a later stop event for the same play can try again.
            var handled = false;
            var sent = false;
            try
            {
                if (_queue.HasPending(user.MediaBrowserUserId))
                {
                    EnqueueScrobble(request, user, "earlier scrobbles for this user are still waiting to be sent");
                    handled = true;
                    return;
                }

                _logger.LogInformation("Submitting scrobble: user={User}, payload={Payload}", user.Username, DescribeRequest(request));

                var response = await Post<ScrobbleRequest, ScrobbleResponse>(request).ConfigureAwait(false);

                switch (Classify(response))
                {
                    case SubmitOutcome.Retry:
                        EnqueueScrobble(request, user, response == null
                            ? "no usable response from Last.fm"
                            : $"error {response.ErrorCode}: {response.Message}");
                        handled = true;
                        return;

                    case SubmitOutcome.InvalidSession:
                        EnqueueScrobble(request, user, "session key rejected");
                        _queue.MarkInvalidSession(user.MediaBrowserUserId, user.SessionKey);
                        _logger.LogWarning("Last.fm rejected the session key for {User}. Scrobbles are kept and will be sent once the account is re-linked on the plugin configuration page.", user.Username);
                        handled = true;
                        return;

                    case SubmitOutcome.Rejected:
                        _logger.LogError("Scrobble failed: user={User}, errorCode={ErrorCode}, message={Message}, hint={Hint}, payload={Payload}", user.Username, response.ErrorCode, response.Message, DescribeApiError(response.ErrorCode), DescribeRequest(request));
                        return;
                }

                // Last.fm reports a silently dropped scrobble with HTTP 200 and no error code.
                // The body carries accepted=0/ignored=1 plus a reason instead.
                // Without this check an ignored scrobble is indistinguishable from an accepted one in the logs.
                var attributes = response.Scrobbles?.Attributes;
                if (attributes != null && attributes.Ignored > 0)
                {
                    var ignoredMessage = response.Scrobbles.Scrobble?.IgnoredMessage;
                    _logger.LogWarning("Scrobble ignored by Last.fm: user={User}, code={Code}, reason={Reason}, payload={Payload}", user.Username, ignoredMessage?.Code, DescribeIgnoredScrobble(ignoredMessage), DescribeRequest(request));
                    return;
                }

                _logger.LogInformation("Scrobble succeeded: user={User}, artist={Artist}, track={Track}, album={Album}", user.Username, request.Artist, request.Track, request.Album);
                handled = true;
                sent = true;
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                _logger.LogWarning(ex, "Could not reach Last.fm to scrobble: user={User}", user.Username);
                EnqueueScrobble(request, user, "could not reach Last.fm");
                handled = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scrobble exception: user={User}, payload={Payload}", user.Username, DescribeRequest(request));
            }
            finally
            {
                if (!handled)
                    _scrobbleCache.Remove(cacheKey);
            }

            // Last.fm is reachable again: send anything other users have waiting.
            if (sent && _queue.Count > 0)
            {
                await FlushQueueAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        private void EnqueueScrobble(ScrobbleRequest request, LastfmUser user, string reason)
        {
            _queue.Enqueue(QueuedScrobble.From(request, user.MediaBrowserUserId));
            _logger.LogWarning("Scrobble queued for retry ({Reason}): user={User}, artist={Artist}, track={Track}. Pending: {Pending}", reason, user.Username, request.Artist, request.Track, _queue.Count);
        }

        /// <summary>
        /// Resubmits queued scrobbles in batches of up to 50, oldest first. Stops as soon as
        /// Last.fm is unavailable or rate limiting, so the remaining scrobbles keep their order.
        /// Concurrent calls return immediately while a flush is running.
        /// </summary>
        public async Task FlushQueueAsync(CancellationToken cancellationToken)
        {
            if (_queue.Count == 0 || LastfmRateLimit.IsLimited(out _))
            {
                return;
            }

            if (!await _flushLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                // Sending these would only get them ignored, so drop them with a clear message instead.
                var expired = _queue.RemoveOlderThan(Helpers.ToTimestamp(DateTime.UtcNow - MaxScrobbleAge));
                foreach (var scrobble in expired)
                {
                    _logger.LogWarning("Dropping queued scrobble older than {Days} days, which Last.fm no longer accepts: user={UserId}, artist={Artist}, track={Track}, playedAt={PlayedAt:o}", MaxScrobbleAge.TotalDays, scrobble.UserId, scrobble.Artist, scrobble.Track, DateTimeOffset.FromUnixTimeSeconds(scrobble.Timestamp));
                }

                foreach (var userId in _queue.UsersWithPending())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var user = UserHelpers.GetUser(userId);
                    if (user == null || string.IsNullOrWhiteSpace(user.SessionKey))
                    {
                        _logger.LogDebug("Keeping queued scrobbles for user {UserId}: no linked Last.fm account", userId);
                        continue;
                    }

                    if (_queue.IsAwaitingReauthentication(userId, user.SessionKey))
                    {
                        _logger.LogDebug("Keeping queued scrobbles for {User} until the Last.fm account is re-linked", user.Username);
                        continue;
                    }

                    if (!await FlushUserAsync(user, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }
                }
            }
            finally
            {
                _flushLock.Release();
            }
        }

        /// <returns>False when Last.fm is unavailable and flushing should stop for now.</returns>
        private async Task<bool> FlushUserAsync(LastfmUser user, CancellationToken cancellationToken)
        {
            var batchSize = BatchScrobbleRequest.MaxBatchSize;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batch = _queue.PeekBatch(user.MediaBrowserUserId, batchSize);
                if (batch.Count == 0)
                {
                    return true;
                }

                var request = new BatchScrobbleRequest
                {
                    Scrobbles = batch.Select(b => b.ToRequest()).ToList(),
                    ApiKey = Strings.Keys.LastfmApiKey,
                    Method = Strings.Methods.Scrobble,
                    SessionKey = user.SessionKey,
                    Secure = true
                };

                BatchScrobbleResponse response;
                try
                {
                    response = await Post<BatchScrobbleRequest, BatchScrobbleResponse>(request).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsTransportFailure(ex))
                {
                    _logger.LogWarning(ex, "Could not reach Last.fm to send {Count} queued scrobble(s) for {User}", batch.Count, user.Username);
                    return false;
                }

                switch (Classify(response))
                {
                    case SubmitOutcome.Ok:
                        _queue.Remove(batch);
                        LogBatchResult(user, batch, response);
                        continue;

                    case SubmitOutcome.Retry:
                        _logger.LogInformation("Last.fm still unavailable ({Error}); {Pending} scrobble(s) stay queued", response == null ? "no usable response" : $"error {response.ErrorCode}", _queue.Count);
                        return false;

                    case SubmitOutcome.InvalidSession:
                        _queue.MarkInvalidSession(user.MediaBrowserUserId, user.SessionKey);
                        _logger.LogWarning("Last.fm rejected the session key for {User}. Queued scrobbles are kept until the account is re-linked.", user.Username);
                        return true;

                    case SubmitOutcome.Rejected:
                        if (batch.Count > 1)
                        {
                            // Send one at a time so a single bad scrobble cannot hold back the rest.
                            _logger.LogWarning("Last.fm rejected a batch of {Count} queued scrobbles (error {ErrorCode}: {Message}); retrying them individually", batch.Count, response.ErrorCode, response.Message);
                            batchSize = 1;
                            continue;
                        }

                        _queue.Remove(batch);
                        _logger.LogError("Dropping queued scrobble rejected by Last.fm: user={User}, errorCode={ErrorCode}, message={Message}, hint={Hint}, payload={Payload}", user.Username, response.ErrorCode, response.Message, DescribeApiError(response.ErrorCode), DescribeRequest(request.Scrobbles[0]));
                        continue;
                }
            }
        }

        private void LogBatchResult(LastfmUser user, IReadOnlyList<QueuedScrobble> batch, BatchScrobbleResponse response)
        {
            var attributes = response.Scrobbles?.Attributes;
            _logger.LogInformation("Sent {Count} queued scrobble(s) for {User}: accepted={Accepted}, ignored={Ignored}. Still pending: {Pending}", batch.Count, user.Username, attributes?.Accepted, attributes?.Ignored, _queue.Count);

            if (attributes == null || attributes.Ignored == 0)
            {
                return;
            }

            var messages = response.Scrobbles.IgnoredMessages();
            for (var i = 0; i < batch.Count && i < messages.Count; i++)
            {
                if (messages[i] is { Code: > 0 } ignored)
                {
                    _logger.LogWarning("Queued scrobble ignored by Last.fm: user={User}, artist={Artist}, track={Track}, code={Code}, reason={Reason}", user.Username, batch[i].Artist, batch[i].Track, ignored.Code, DescribeIgnoredScrobble(ignored));
                }
            }
        }

        public async Task NowPlaying(Audio item, LastfmUser user)
        {
            var artist = item.Artists.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(artist))
            {
                _logger.LogWarning("Skipping now playing update for '{0}': no artist tagged", item.Name);
                return;
            }

            var request = new NowPlayingRequest
            {
                Track = item.Name,
                Artist = artist,

                ApiKey = Strings.Keys.LastfmApiKey,
                Method = Strings.Methods.NowPlaying,
                SessionKey = user.SessionKey,
                Secure = true
            };

            if (!string.IsNullOrWhiteSpace(item.Album))
            {
                request.Album = item.Album;
            }
            if (item.ProviderIds.ContainsKey("MusicBrainzTrack"))
            {
                request.MbId = item.ProviderIds["MusicBrainzTrack"];
            }
            var albumArtist = item.AlbumArtists.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(albumArtist) && albumArtist != request.Artist)
            {
                request.AlbumArtist = albumArtist;
            }

            // Add duration
            if (item.RunTimeTicks != null)
                request.Duration = Convert.ToInt32(TimeSpan.FromTicks((long)item.RunTimeTicks).TotalSeconds);

            try
            {
                var response = await Post<NowPlayingRequest, ScrobbleResponse>(request);
                if (response != null && !response.IsError())
                {
                    _logger.LogInformation("{0} is now playing artist={1}, track={2}, album={3}", user.Username, request.Artist, request.Track, request.Album);
                    return;
                }

                // Now playing updates are not retried (https://www.last.fm/api/scrobbling), so
                // skipping one while rate limited is expected rather than an error.
                if (response?.ErrorCode == LastfmRateLimit.RateLimitErrorCode)
                {
                    _logger.LogDebug("Skipped now playing for {Track}: {Message}", item.Name, response.Message);
                    return;
                }

                _logger.LogError("Failed to send now playing for track: {Track}, errorCode={ErrorCode}, message={Message}, hint={Hint}", item.Name, response?.ErrorCode, response?.Message, DescribeApiError(response?.ErrorCode ?? 0));
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to send now playing for track: ex={0}, name={1}, track={2}, artist={3}, album={4}, albumArtist={5}, mbid={6}", ex, item.Name, request.Track, request.Artist, request.Album, request.AlbumArtist, request.MbId);
            }
        }

        /// <summary>
        /// Loves or unloves a track
        /// </summary>
        /// <param name="item">The track</param>
        /// <param name="user">The Lastfm User</param>
        /// <param name="love">If the track is loved or not</param>
        /// <returns></returns>
        public async Task<bool> LoveTrack(Audio item, LastfmUser user, bool love = true)
        {
            var artist = item.Artists.FirstOrDefault();
            if (artist == null) {
                return false;
            }

            var request = new TrackLoveRequest
            {
                Artist = artist,
                Track = item.Name,

                ApiKey = Strings.Keys.LastfmApiKey,
                Method = love ? Strings.Methods.TrackLove : Strings.Methods.TrackUnlove,
                SessionKey = user.SessionKey,
                Secure = true
            };

            try
            {
                //Send the request
                var response = await Post<TrackLoveRequest, BaseResponse>(request);

                if (response != null && !response.IsError())
                {
                    _logger.LogInformation("{Username} {LovedState}loved track '{Track}'", user.Username,  love ? "" : "un", item.Name);
                    return true;
                }

                _logger.LogError("{Username} Failed to {LoveState}love track '{Track}' - errorCode={ErrorCode}, message={Message}, hint={Hint}", user.Username, love ? "" : "un", item.Name, response?.ErrorCode, response?.Message, DescribeApiError(response?.ErrorCode ?? 0));
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError("{0} Failed to love = {3} track '{2}' - {1}", user.Username, ex, item.Name, love);
                return false;
            }
        }

        /// <summary>
        /// Unlove a track. This is the same as LoveTrack with love as false
        /// </summary>
        /// <param name="item">The track</param>
        /// <param name="user">The Lastfm User</param>
        /// <returns></returns>
        public async Task<bool> UnloveTrack(Audio item, LastfmUser user)
        {
            return await LoveTrack(item, user, false);
        }

        public async Task<LovedTracksResponse> GetLovedTracks(LastfmUser user, CancellationToken cancellationToken, int page)
        {
            var request = new GetLovedTracksRequest
            {
                User = user.Username,
                ApiKey = Strings.Keys.LastfmApiKey,
                Method = Strings.Methods.GetLovedTracks,
                Limit = 1000, // {"error":6,"message":"limit param out of bounds (1-1000)"}
                Page = page,
                Secure = true
            };

            return await Get<GetLovedTracksRequest, LovedTracksResponse>(request, cancellationToken);
        }

        public async Task<GetTracksResponse> GetTracks(LastfmUser user, MusicArtist artist, CancellationToken cancellationToken)
        {
            var request = new GetTracksRequest
            {
                User = user.Username,
                Artist = artist.Name,
                ApiKey = Strings.Keys.LastfmApiKey,
                Method = Strings.Methods.GetTracks,
                Limit = 1000,
                Secure = true
            };

            return await Get<GetTracksRequest, GetTracksResponse>(request, cancellationToken);
        }

        public async Task<GetTracksResponse> GetTracks(LastfmUser user, CancellationToken cancellationToken, int page = 0, int limit = 200)
        {
            var request = new GetTracksRequest
            {
                User = user.Username,
                ApiKey = Strings.Keys.LastfmApiKey,
                Method = Strings.Methods.GetTracks,
                Limit = limit,
                Page = page,
                Secure = true

            };

            return await Get<GetTracksRequest, GetTracksResponse>(request, cancellationToken);
        }

        /// <summary>
        /// Renders the request exactly as it is sent to Last.fm, url encoded, with the
        /// credentials removed. The encoding matters: characters that are invisible in a log,
        /// like non breaking spaces or zero width joiners, show up as escape sequences, and
        /// the result can be replayed against the API as is.
        /// </summary>
        private static string DescribeRequest(BaseRequest request)
        {
            var parts = request.ToDictionary()
                .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value))
                .Where(kvp => kvp.Key != "api_key" && kvp.Key != "sk" && kvp.Key != "api_sig")
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => string.Format("{0}={1}", kvp.Key, Uri.EscapeDataString(kvp.Value)));

            return string.Join("&", parts);
        }

        /// <summary>
        /// Explains the API error codes that need action from the user or that Last.fm says are temporary.
        /// See https://www.last.fm/api/scrobbling and https://www.last.fm/api/show/track.scrobble.
        /// </summary>
        internal static string DescribeApiError(int errorCode) => errorCode switch
        {
            9 => "Session key is no longer valid. Re-link this user's Last.fm account on the plugin configuration page.",
            11 or 16 => "Last.fm is temporarily unavailable.",
            26 => "The plugin's API key has been suspended by Last.fm.",
            29 => "Rate limit exceeded: this server has made too many requests to Last.fm in a short period.",
            0 => "No response or unparseable response from Last.fm.",
            _ => "Request rejected by Last.fm; retrying unchanged will not help."
        };

        /// <summary>
        /// Last.fm leaves the ignoredMessage text empty in some responses, so fall back to
        /// the documented meaning of the code.
        /// </summary>
        private static string DescribeIgnoredScrobble(IgnoredMessage ignoredMessage)
        {
            if (!string.IsNullOrWhiteSpace(ignoredMessage?.Text))
            {
                return ignoredMessage.Text;
            }

            // https://www.last.fm/api/show/track.scrobble
            return ignoredMessage?.Code switch
            {
                1 => "Artist was ignored",
                2 => "Track was ignored",
                3 => "Timestamp was too old",
                4 => "Timestamp was too new",
                5 => "Daily scrobble limit exceeded",
                _ => "Unknown reason"
            };
        }

        /// <summary>
        /// Checks for duplicate scrobble and updates cache if not duplicate.
        /// Even though MemoryCache is thread-safe, we use the _scrobbleLock to ensure thread safety for the whole check-and-set operation.
        /// The method also updates the cache with the new scrobble if it's not a duplicate.
        /// Returns true if duplicate, false otherwise.
        /// </summary>
        private bool CheckAndUpdateScrobbleCache(string cacheKey)
        {
            lock (_scrobbleLock)
            {
                if (_scrobbleCache.TryGetValue(cacheKey, out _))
                {
                    _logger.LogInformation("Playback already submitted or in flight: {PlaybackKey}. Skipping duplicate stop.", cacheKey);
                    return true;
                }
                _scrobbleCache.Set(cacheKey, true, DuplicateScrobbleTTL);
                return false;
            }
        }
    }
}
