namespace Jellyfin.Plugin.Lastfm.Resources
{
    public static class Strings
    {
        public static class Endpoints
        {
            public static string LastfmApi  = "ws.audioscrobbler.com";
        }

        public static class Methods
        {
            // Last.FM API specs located at https://www.last.fm/api
            public static string Scrobble         = "track.scrobble";
            public static string NowPlaying       = "track.updateNowPlaying";
            public static string GetMobileSession = "auth.getMobileSession";
            public static string TrackLove        = "track.love";
            public static string TrackUnlove      = "track.unlove";
            public static string GetLovedTracks   = "user.getLovedTracks";
            public static string GetTracks        = "library.getTracks";
        }

        public static class Keys
        {
            public static string LastfmApiKey = "00d6ea99f92a9c9c9686291eddc9c533";
            public static string LastfmApiSecret = "da27c3b9f8434c46d125a417af87f10d";
        }
    }
}
