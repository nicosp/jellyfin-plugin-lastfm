namespace Jellyfin.Plugin.Lastfm.Models.Requests
{
    using System.Collections.Generic;
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
            var scrobbleRequest = new Dictionary<string, string>(base.ToDictionary())
            {
                { "track",     Track },
                { "artist",    Artist },
                { "timestamp", Timestamp.ToString() },
            };
            if (!string.IsNullOrWhiteSpace(Album))
            {
                scrobbleRequest.Add("album", Album);
            }
            if (!string.IsNullOrWhiteSpace(MbId))
            {
                scrobbleRequest.Add("mbid", MbId);
            }
            if (!string.IsNullOrWhiteSpace(AlbumArtist))
            {
                scrobbleRequest.Add("albumArtist", AlbumArtist);
            }
            if (Duration is > 0)
            {
                scrobbleRequest.Add("duration", Duration.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            if (TrackNumber is > 0)
            {
                scrobbleRequest.Add("trackNumber", TrackNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            return scrobbleRequest;
        }
    }
}
