using Microsoft.Extensions.Logging;
using System.Net.Http;
using Microsoft.AspNetCore.Mvc;
using System;

namespace Jellyfin.Plugin.Lastfm.Api
{

    [ApiController]
    [Route("Lastfm/Login")]
    public class RestApi : ControllerBase
    {
        private readonly LastfmApiClient _apiClient;
        private readonly ILogger<RestApi> _logger;
        private static readonly object _apiHostLock = new();

        public RestApi(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<RestApi>();
            _apiClient = new LastfmApiClient(httpClientFactory, _logger);
        }

        [HttpPost]
        [Consumes("application/json")]
        public object CreateMobileSession([FromBody] LastFMUser lastFMUser)
        {
            _logger.LogInformation("Fetching Last.fm mobilesession auth for Username={0}", lastFMUser.Username);
            return ExecuteWithApiHostOverride(lastFMUser.ApiHost, () => _apiClient.RequestSession(lastFMUser.Username, lastFMUser.Password).Result);
        }

        private static object ExecuteWithApiHostOverride(string apiHost, Func<object> action)
        {
            lock (_apiHostLock)
            {
                var config = Plugin.Instance?.PluginConfiguration;
                if (config == null)
                {
                    return action();
                }

                var originalHost = config.LastfmApiHost;

                if (!string.IsNullOrWhiteSpace(apiHost))
                {
                    config.LastfmApiHost = apiHost;
                }

                try
                {
                    return action();
                }
                finally
                {
                    config.LastfmApiHost = originalHost;
                }
            }
        }
    }

    public class LastFMUser
    {
        public string Username { get; set; }
        public string Password { get; set; }
        public string ApiHost { get; set; }
    }
}
