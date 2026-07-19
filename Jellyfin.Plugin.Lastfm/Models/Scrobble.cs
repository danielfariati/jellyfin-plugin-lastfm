namespace Jellyfin.Plugin.Lastfm.Models
{
    using System.Text.Json.Serialization;

    public class Scrobbles
    {
        [JsonPropertyName("@attr")]
        public ScrobbleAttributes Attributes { get; set; }

        // We only ever submit a single scrobble per request, so Last.fm returns an
        // object here rather than an array.
        [JsonPropertyName("scrobble")]
        public ScrobbleResult Scrobble { get; set; }
    }

    public class ScrobbleAttributes
    {
        // https://www.last.fm/api/show/track.scrobble
        // accepted : Number of accepted scrobbles
        [JsonPropertyName("accepted")]
        public int Accepted { get; set; }

        // https://www.last.fm/api/show/track.scrobble
        // ignored : Number of ignored scrobbles (see ignoredMessage for details)
        [JsonPropertyName("ignored")]
        public int Ignored { get; set; }
    }

    public class ScrobbleResult
    {
        [JsonPropertyName("ignoredMessage")]
        public IgnoredMessage IgnoredMessage { get; set; }
    }

    public class IgnoredMessage
    {
        // https://www.last.fm/api/show/track.scrobble
        // 0 : Not ignored;
        // 1 : Artist was ignored;
        // 2 : Track was ignored;
        // 3 : Timestamp was too old;
        // 4 : Timestamp was too new;
        // 5 : Daily scrobble limit exceeded;
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("#text")]
        public string Text { get; set; }
    }
}
