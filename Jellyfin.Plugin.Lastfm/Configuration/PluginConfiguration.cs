namespace Jellyfin.Plugin.Lastfm.Configuration
{
    using Models;
    using MediaBrowser.Model.Plugins;
    using Resources;

    /// <summary>
    /// Class PluginConfiguration
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        public LastfmUser[] LastfmUsers { get; set; }
        public string LastfmApiHost { get; set; }
        public string ApiKey { get; set; }
        public string ApiSecret { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="PluginConfiguration" /> class.
        /// </summary>
        public PluginConfiguration()
        {
            LastfmUsers = [];
            LastfmApiHost = Strings.Endpoints.LastfmApi;
            ApiKey = Strings.Keys.LastfmApiKey;
            ApiSecret = Strings.Keys.LastfmApiSecret;
        }
    }
}
