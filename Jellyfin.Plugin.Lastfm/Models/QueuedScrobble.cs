namespace Jellyfin.Plugin.Lastfm.Models
{
    using System;
    using Requests;

    /// <summary>
    /// A scrobble waiting to be (re)submitted to Last.fm. Persisted to disk as JSON.
    /// </summary>
    public class QueuedScrobble
    {
        /// <summary>The Jellyfin user the scrobble belongs to.</summary>
        public Guid UserId { get; set; }

        public string Track { get; set; }
        public string Artist { get; set; }
        public int Timestamp { get; set; }
        public string Album { get; set; }
        public string AlbumArtist { get; set; }
        public string MbId { get; set; }
        public int? Duration { get; set; }
        public int? TrackNumber { get; set; }

        public static QueuedScrobble From(ScrobbleRequest request, Guid userId) => new()
        {
            UserId = userId,
            Track = request.Track,
            Artist = request.Artist,
            Timestamp = request.Timestamp,
            Album = request.Album,
            AlbumArtist = request.AlbumArtist,
            MbId = request.MbId,
            Duration = request.Duration,
            TrackNumber = request.TrackNumber
        };

        public ScrobbleRequest ToRequest() => new()
        {
            Track = Track,
            Artist = Artist,
            Timestamp = Timestamp,
            Album = Album,
            AlbumArtist = AlbumArtist,
            MbId = MbId,
            Duration = Duration,
            TrackNumber = TrackNumber
        };
    }
}
