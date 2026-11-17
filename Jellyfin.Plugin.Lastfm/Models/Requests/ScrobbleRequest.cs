namespace Jellyfin.Plugin.Lastfm.Models.Requests
{
    using System.Collections.Generic;
    using System.Globalization;
    using System.Runtime.Serialization;

    [DataContract]
    public class ScrobbleRequest : BaseAuthedRequest
    {
        // API docs for scrobbling located at https://www.last.fm/api/show/track.scrobble
        // Track, Artist, and Timestamp are required
        // Album, AlbumArtist, MbId, Duration and TrackNumber are optional.
        public string Track { get; set; }
        public string Artist { get; set; }
        public int Timestamp { get; set; }
        public string Album { get; set; }
        public string AlbumArtist { get; set; }
        public string MbId { get; set; }
        public int? Duration { get; set; }
        public int? TrackNumber { get; set; }

        public override Dictionary<string, string> ToDictionary()
        {
            var data = new Dictionary<string, string>(base.ToDictionary());
            foreach (var field in TrackFields())
            {
                data.Add(field.Key, field.Value);
            }

            return data;
        }

        /// <summary>
        /// The per-track parameters, without the method, API key or session key.
        /// </summary>
        public Dictionary<string, string> TrackFields()
        {
            var fields = new Dictionary<string, string>
            {
                { "track",     Track },
                { "artist",    Artist },
                { "timestamp", Timestamp.ToString(CultureInfo.InvariantCulture) },
            };
            if (!string.IsNullOrWhiteSpace(Album))
            {
                fields.Add("album", Album);
            }
            if (!string.IsNullOrWhiteSpace(MbId))
            {
                fields.Add("mbid", MbId);
            }
            if (!string.IsNullOrWhiteSpace(AlbumArtist))
            {
                fields.Add("albumArtist", AlbumArtist);
            }
            if (Duration is > 0)
            {
                fields.Add("duration", Duration.Value.ToString(CultureInfo.InvariantCulture));
            }
            if (TrackNumber is > 0)
            {
                fields.Add("trackNumber", TrackNumber.Value.ToString(CultureInfo.InvariantCulture));
            }

            return fields;
        }
    }

    /// <summary>
    /// Submits up to 50 scrobbles in one track.scrobble request, using array notation
    /// (artist[0], track[0], ...). See https://www.last.fm/api/show/track.scrobble.
    /// </summary>
    public class BatchScrobbleRequest : BaseAuthedRequest
    {
        public const int MaxBatchSize = 50;

        public IReadOnlyList<ScrobbleRequest> Scrobbles { get; set; } = new List<ScrobbleRequest>();

        public override Dictionary<string, string> ToDictionary()
        {
            var data = new Dictionary<string, string>(base.ToDictionary());
            for (var i = 0; i < Scrobbles.Count; i++)
            {
                foreach (var field in Scrobbles[i].TrackFields())
                {
                    data.Add(string.Format(CultureInfo.InvariantCulture, "{0}[{1}]", field.Key, i), field.Value);
                }
            }

            return data;
        }
    }
}
