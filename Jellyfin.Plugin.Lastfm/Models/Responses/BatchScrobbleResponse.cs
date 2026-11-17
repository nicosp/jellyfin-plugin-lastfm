namespace Jellyfin.Plugin.Lastfm.Models.Responses
{
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Response to a batched track.scrobble. Last.fm returns "scrobble" as an object for a
    /// single scrobble and as an array for several, so it is read as raw JSON.
    /// </summary>
    public class BatchScrobbleResponse : BaseResponse
    {
        [JsonPropertyName("scrobbles")]
        public BatchScrobbles Scrobbles { get; set; }
    }

    public class BatchScrobbles
    {
        [JsonPropertyName("@attr")]
        public ScrobbleAttributes Attributes { get; set; }

        [JsonPropertyName("scrobble")]
        public JsonElement Scrobble { get; set; }

        /// <summary>
        /// The ignored message of each scrobble in the batch, in submission order.
        /// </summary>
        public IReadOnlyList<IgnoredMessage> IgnoredMessages()
        {
            var results = new List<IgnoredMessage>();
            if (Scrobble.ValueKind == JsonValueKind.Object)
            {
                results.Add(ReadIgnoredMessage(Scrobble));
            }
            else if (Scrobble.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in Scrobble.EnumerateArray())
                {
                    results.Add(ReadIgnoredMessage(item));
                }
            }

            return results;
        }

        private static IgnoredMessage ReadIgnoredMessage(JsonElement scrobble)
        {
            if (scrobble.ValueKind != JsonValueKind.Object || !scrobble.TryGetProperty("ignoredMessage", out var ignored))
            {
                return null;
            }

            return ignored.Deserialize<IgnoredMessage>(new JsonSerializerOptions
            {
                NumberHandling = JsonNumberHandling.AllowReadingFromString
            });
        }
    }
}
