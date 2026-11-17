namespace Jellyfin.Plugin.Lastfm.Utils
{
    using MediaBrowser.Controller.Entities.Audio;
    using Models;
    using Resources;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using MediaBrowser.Common.Net;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Api;
    using Microsoft.Extensions.Logging;
    using System.Security.Cryptography;
    using System.Text;

    public static class Helpers
    {
        private static readonly string UserAgentProduct =
            "Jellyfin-Plugin-Lastfm/" + typeof(Helpers).Assembly.GetName().Version;

        /// <summary>
        /// Creates an HTTP client for Last.fm requests. Last.fm asks clients to send an
        /// identifiable User-Agent on every request (https://www.last.fm/api/intro).
        /// Jellyfin's default named client already identifies the server; this adds the plugin.
        /// </summary>
        public static HttpClient CreateHttpClient(IHttpClientFactory httpClientFactory)
        {
            var client = httpClientFactory.CreateClient(NamedClient.Default);
            client.DefaultRequestHeaders.UserAgent.TryParseAdd(UserAgentProduct);
            return client;
        }

        public static string CreateMd5Hash(string input)
        {
            // Use input string to calculate MD5 hash
            var md5 = MD5.Create();

            var inputBytes = Encoding.UTF8.GetBytes(input);
            var hashBytes = md5.ComputeHash(inputBytes);

            // Convert the byte array to hexadecimal string
            var sb = new StringBuilder();

            foreach (byte b in hashBytes)
                sb.Append(b.ToString("X2"));

            return sb.ToString();
        }

        /// <summary>
        /// GETs a Last.fm API URL for the metadata providers and returns the body, or null
        /// when Last.fm is rate limiting this server (HTTP 429 or error 29) or a pause is in effect.
        /// </summary>
        public static async Task<string> GetLastfmJsonAsync(IHttpClientFactory httpClientFactory, string url, ILogger logger, CancellationToken cancellationToken)
        {
            if (LastfmRateLimit.IsLimited(out var remaining))
            {
                logger.LogDebug("Last.fm rate limit in effect for another {Remaining}; skipping metadata lookup", remaining);
                return null;
            }

            using var response = await CreateHttpClient(httpClientFactory).GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (LastfmRateLimit.IsRateLimitResponse(response))
            {
                var pause = LastfmRateLimit.RegisterLimit(response);
                logger.LogWarning("Last.fm rate limit hit (HTTP 429) during metadata lookup. Pausing all Last.fm requests for {Pause}", pause);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (IsRateLimitErrorBody(body))
            {
                var pause = LastfmRateLimit.RegisterLimit(response);
                logger.LogWarning("Last.fm rate limit hit (error 29) during metadata lookup. Pausing all Last.fm requests for {Pause}", pause);
                return null;
            }

            LastfmRateLimit.RegisterSuccess();
            return body;
        }

        private static bool IsRateLimitErrorBody(string body)
        {
            if (string.IsNullOrWhiteSpace(body) || !body.Contains("\"error\"", StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                return document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.Number
                    && error.GetInt32() == LastfmRateLimit.RateLimitErrorCode;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        public static void AppendSignature(ref Dictionary<string, string> data)
        {
            data.Add("api_sig", CreateSignature(data));
        }

        public static int ToTimestamp(DateTime date)
        {
            return Convert.ToInt32((date - new DateTime(1970, 1, 1)).TotalSeconds);
        }

        public static DateTime FromTimestamp(double timestamp)
        {
            var date = new DateTime(1970, 1, 1, 0, 0, 0, 0);

            return date.AddSeconds(timestamp).ToLocalTime();
        }

        public static int CurrentTimestamp()
        {
            return ToTimestamp(DateTime.UtcNow);
        }

        public static string DictionaryToQueryString(Dictionary<string, string> data)
        {
            return String.Join("&", data.Where(k => !String.IsNullOrWhiteSpace(k.Value)).Select(kvp => String.Format("{0}={1}", Uri.EscapeDataString(kvp.Key), Uri.EscapeDataString(kvp.Value))));
        }

        private static string CreateSignature(Dictionary<string, string> data)
        {
            var s = new StringBuilder();

            // Last.fm requires parameter names sorted in ASCII order, e.g. artist[10] before artist[1].
            foreach (var item in data.OrderBy(x => x.Key, StringComparer.Ordinal))
                s.Append(String.Format("{0}{1}", item.Key, item.Value));

            //Append secret
            s.Append(Strings.Keys.LastfmApiSecret);

            return CreateMd5Hash(s.ToString());
        }

        //The nuget doesn't seem to have GetProviderId for artists
        public static string GetMusicBrainzArtistId(MusicArtist artist)
        {

            if (artist.ProviderIds == null)
            {
                return null;
            }

            if (artist.ProviderIds.TryGetValue("MusicBrainzArtist", out string mbArtistId))
            {
                return mbArtistId;
            }
            return null;
        }

    }
}
