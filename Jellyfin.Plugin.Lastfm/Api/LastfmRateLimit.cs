namespace Jellyfin.Plugin.Lastfm.Api
{
    using System;
    using System.Net;
    using System.Net.Http;

    /// <summary>
    /// Tracks Last.fm rate limiting (HTTP 429 or API error 29) for the whole server.
    /// </summary>
    /// <remarks>
    /// Last.fm limits per originating IP, so every client in the plugin shares this state.
    /// While limited, no requests are sent: scrobbles are queued and other calls fail fast.
    /// The pause honours a Retry-After header when present, and otherwise backs off
    /// exponentially from <see cref="InitialBackoff"/> up to <see cref="MaxBackoff"/>.
    /// </remarks>
    public static class LastfmRateLimit
    {
        /// <summary>Last.fm API error code for "Rate limit exceeded".</summary>
        public const int RateLimitErrorCode = 29;

        public static readonly TimeSpan InitialBackoff = TimeSpan.FromMinutes(1);
        public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

        private static readonly object Lock = new();
        private static DateTime _limitedUntilUtc = DateTime.MinValue;
        private static int _consecutiveLimits;

        /// <summary>Clock used for all decisions. Replaceable for tests.</summary>
        internal static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <summary>
        /// Returns true while requests must not be sent, with the time left until they may.
        /// </summary>
        public static bool IsLimited(out TimeSpan remaining)
        {
            lock (Lock)
            {
                remaining = _limitedUntilUtc - UtcNow();
                if (remaining > TimeSpan.Zero)
                {
                    return true;
                }

                remaining = TimeSpan.Zero;
                return false;
            }
        }

        /// <summary>True when the response is a rate-limit response (HTTP 429).</summary>
        public static bool IsRateLimitResponse(HttpResponseMessage response) =>
            response != null && response.StatusCode == HttpStatusCode.TooManyRequests;

        /// <summary>
        /// Records a rate-limit response and returns how long requests are paused.
        /// </summary>
        public static TimeSpan RegisterLimit(HttpResponseMessage response)
        {
            lock (Lock)
            {
                _consecutiveLimits++;

                var delay = GetRetryAfter(response) ?? ExponentialBackoff(_consecutiveLimits);
                if (delay > MaxBackoff)
                {
                    delay = MaxBackoff;
                }

                var until = UtcNow() + delay;
                if (until > _limitedUntilUtc)
                {
                    _limitedUntilUtc = until;
                }

                return _limitedUntilUtc - UtcNow();
            }
        }

        /// <summary>Records a request that was not rate limited, resetting the backoff.</summary>
        public static void RegisterSuccess()
        {
            lock (Lock)
            {
                _consecutiveLimits = 0;
            }
        }

        /// <summary>Clears all state. For tests.</summary>
        internal static void Reset()
        {
            lock (Lock)
            {
                _limitedUntilUtc = DateTime.MinValue;
                _consecutiveLimits = 0;
                UtcNow = () => DateTime.UtcNow;
            }
        }

        private static TimeSpan ExponentialBackoff(int attempt)
        {
            // 1, 2, 4, 8, 16, 30, 30 ... minutes
            var factor = Math.Pow(2, Math.Min(attempt - 1, 10));
            return TimeSpan.FromTicks((long)Math.Min(InitialBackoff.Ticks * factor, MaxBackoff.Ticks));
        }

        private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
        {
            var retryAfter = response?.Headers.RetryAfter;
            if (retryAfter == null)
            {
                return null;
            }

            if (retryAfter.Delta is { } delta && delta > TimeSpan.Zero)
            {
                return delta;
            }

            if (retryAfter.Date is { } date)
            {
                var wait = date.UtcDateTime - UtcNow();
                return wait > TimeSpan.Zero ? wait : null;
            }

            return null;
        }
    }
}
