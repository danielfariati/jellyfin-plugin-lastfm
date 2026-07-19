namespace Jellyfin.Plugin.Lastfm.Api
{
    using MediaBrowser.Controller.Entities.Audio;
    using Models;
    using Models.Requests;
    using Models.Responses;
    using Resources;
    using System;
    using Microsoft.Extensions.Caching.Memory;
    using System.Linq;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Utils;
    using Microsoft.Extensions.Logging;

    public class LastfmApiClient : BaseLastfmApiClient
    {
        private readonly ILogger _logger;

        private static readonly TimeSpan DuplicateScrobbleTTL = TimeSpan.FromSeconds(15);
        private readonly MemoryCache _scrobbleCache = new(new MemoryCacheOptions());
        private readonly object _scrobbleLock = new();

        public LastfmApiClient(IHttpClientFactory httpClientFactory, ILogger logger) : base(httpClientFactory, logger)
        {
            _logger = logger;
        }



        public async Task<MobileSessionResponse> RequestSession(string username, string password)
        {
            //Build request object
            var request = new MobileSessionRequest
            {
                Username = username,
                Password = password,

                ApiKey = Strings.Keys.LastfmApiKey,
                Method = Strings.Methods.GetMobileSession,
                Secure = true
            };

            var response = await Post<MobileSessionRequest, MobileSessionResponse>(request);

            if (ShouldRetryWithLegacyAuthToken(response))
            {
                _logger.LogInformation("Retrying mobile session auth with legacy authToken flow for host={Host}", Plugin.Instance?.PluginConfiguration?.LastfmApiHost);

                request.Password = null;
                request.AuthToken = BuildLegacyAuthToken(username, password);
                response = await Post<MobileSessionRequest, MobileSessionResponse>(request);
            }


            return response;
        }

        private static string BuildLegacyAuthToken(string username, string password)
        {
            var passwordHash = Helpers.CreateMd5Hash(password).ToLowerInvariant();
            return Helpers.CreateMd5Hash(username + passwordHash).ToLowerInvariant();
        }

        private static bool ShouldRetryWithLegacyAuthToken(MobileSessionResponse response)
        {
            if (response == null || !response.IsError())
            {
                return false;
            }

            var configuredHost = Plugin.Instance?.PluginConfiguration?.LastfmApiHost;
            var isLibreHost = !string.IsNullOrWhiteSpace(configuredHost) && configuredHost.Contains("libre.fm", StringComparison.OrdinalIgnoreCase);
            if (!isLibreHost)
            {
                return false;
            }

            if (response.ErrorCode == 6)
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(response.Message)
                && response.Message.Contains("missing a required parameter", StringComparison.OrdinalIgnoreCase);
        }

        public async Task Scrobble(Audio item, LastfmUser user, DateTime playbackStartTime)
        {
            if (CheckAndUpdateScrobbleCache(user.Username, item.Id.ToString()))
            {
                return;
            }

            // API docs -> https://www.last.fm/api/show/track.scrobble
            // Timestamp must be the time the track started playing (UTC), not the
            // time the scrobble is submitted.
            var request = new ScrobbleRequest
            {
                Track = item.Name,
                Artist = item.Artists.First(),
                Timestamp = Helpers.ToTimestamp(playbackStartTime),

                ApiKey = Strings.Keys.LastfmApiKey,
                Method = Strings.Methods.Scrobble,
                SessionKey = user.SessionKey,
                Secure = true
            };

            if (!string.IsNullOrWhiteSpace(item.Album))
            {
                request.Album = item.Album;
            }
            if (item.ProviderIds.ContainsKey("MusicBrainzTrack"))
            {
                request.MbId = item.ProviderIds["MusicBrainzTrack"];
            }
            var albumArtist = item.AlbumArtists.First();
            if (!string.IsNullOrWhiteSpace(albumArtist) && albumArtist != request.Artist)
            {
                request.AlbumArtist = albumArtist;
            }

            try
            {
                _logger.LogInformation("Submitting scrobble: user={User}, payload={Payload}", user.Username, DescribeRequest(request));

                // Send the request
                var response = await Post<ScrobbleRequest, ScrobbleResponse>(request);

                if (response == null)
                {
                    _logger.LogError("Scrobble failed with null response: user={User}, payload={Payload}", user.Username, DescribeRequest(request));
                    return;
                }

                if (response.IsError())
                {
                    _logger.LogError("Scrobble failed: user={User}, errorCode={ErrorCode}, message={Message}, payload={Payload}", user.Username, response.ErrorCode, response.Message, DescribeRequest(request));
                    return;
                }

                // Last.fm reports a silently dropped scrobble with HTTP 200 and no error code.
                // The body carries accepted=0/ignored=1 plus a reason instead.
                // Without this check an ignored scrobble is indistinguishable from an accepted one in the logs.
                var attributes = response.Scrobbles?.Attributes;
                if (attributes != null && attributes.Ignored > 0)
                {
                    var ignoredMessage = response.Scrobbles.Scrobble?.IgnoredMessage;
                    _logger.LogWarning("Scrobble ignored by Last.fm: user={User}, code={Code}, reason={Reason}, payload={Payload}", user.Username, ignoredMessage?.Code, DescribeIgnoredScrobble(ignoredMessage), DescribeRequest(request));
                    return;
                }

                _logger.LogInformation("Scrobble succeeded: user={User}, artist={Artist}, track={Track}, album={Album}", user.Username, request.Artist, request.Track, request.Album);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scrobble exception: user={User}, payload={Payload}", user.Username, DescribeRequest(request));
            }
        }

        public async Task NowPlaying(Audio item, LastfmUser user)
        {
            var request = new NowPlayingRequest
            {
                Track = item.Name,
                Artist = item.Artists.First(),

                ApiKey = Strings.Keys.LastfmApiKey,
                Method = Strings.Methods.NowPlaying,
                SessionKey = user.SessionKey,
                Secure = true
            };


            if (!string.IsNullOrWhiteSpace(item.Album))
            {
                request.Album = item.Album;
            }
            if (item.ProviderIds.ContainsKey("MusicBrainzTrack"))
            {
                request.MbId = item.ProviderIds["MusicBrainzTrack"];
            }
            var albumArtist = item.AlbumArtists.First();
            if (!string.IsNullOrWhiteSpace(albumArtist) && albumArtist != request.Artist)
            {
                request.AlbumArtist = albumArtist;
            }

            // Add duration
            if (item.RunTimeTicks != null)
                request.Duration = Convert.ToInt32(TimeSpan.FromTicks((long)item.RunTimeTicks).TotalSeconds);

            try
            {
                var response = await Post<NowPlayingRequest, ScrobbleResponse>(request);
                if (response != null && !response.IsError())
                {
                    _logger.LogInformation("{0} is now playing artist={1}, track={2}, album={3}", user.Username, request.Artist, request.Track, request.Album);
                    return;
                }

                _logger.LogError("Failed to send now playing for track: {0}", item.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to send now playing for track: ex={0}, name={1}, track={2}, artist={3}, album={4}, albumArtist={5}, mbid={6}", ex, item.Name, request.Track, request.Artist, request.Album, request.AlbumArtist, request.MbId);
            }
        }

        /// <summary>
        /// Loves or unloves a track
        /// </summary>
        /// <param name="item">The track</param>
        /// <param name="user">The Lastfm User</param>
        /// <param name="love">If the track is loved or not</param>
        /// <returns></returns>
        public async Task<bool> LoveTrack(Audio item, LastfmUser user, bool love = true)
        {
            var artist = item.Artists.FirstOrDefault();
            if (artist == null) {
                return false;
            }

            var request = new TrackLoveRequest
            {
                Artist = artist,
                Track = item.Name,

                ApiKey = Strings.Keys.LastfmApiKey,
                Method = love ? Strings.Methods.TrackLove : Strings.Methods.TrackUnlove,
                SessionKey = user.SessionKey,
                Secure = true
            };

            try
            {
                //Send the request
                var response = await Post<TrackLoveRequest, BaseResponse>(request);

                if (response != null && !response.IsError())
                {
                    _logger.LogInformation("{Username} {LovedState}loved track '{Track}'", user.Username,  love ? "" : "un", item.Name);
                    return true;
                }

                _logger.LogError("{Username} Failed to {LoveState}love track '{Track}' - {Response}", user.Username, love ? "" : "un", item.Name, response.Message);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError("{0} Failed to love = {3} track '{2}' - {1}", user.Username, ex, item.Name, love);
                return false;
            }
        }

        /// <summary>
        /// Unlove a track. This is the same as LoveTrack with love as false
        /// </summary>
        /// <param name="item">The track</param>
        /// <param name="user">The Lastfm User</param>
        /// <returns></returns>
        public async Task<bool> UnloveTrack(Audio item, LastfmUser user)
        {
            return await LoveTrack(item, user, false);
        }

        public async Task<LovedTracksResponse> GetLovedTracks(LastfmUser user, CancellationToken cancellationToken, int page)
        {
            var request = new GetLovedTracksRequest
            {
                User = user.Username,
                ApiKey = Strings.Keys.LastfmApiKey,
                Method = Strings.Methods.GetLovedTracks,
                Limit = 1000, // {"error":6,"message":"limit param out of bounds (1-1000)"}
                Page = page,
                Secure = true
            };

            return await Get<GetLovedTracksRequest, LovedTracksResponse>(request, cancellationToken);
        }

        public async Task<GetTracksResponse> GetTracks(LastfmUser user, MusicArtist artist, CancellationToken cancellationToken)
        {
            var request = new GetTracksRequest
            {
                User = user.Username,
                Artist = artist.Name,
                ApiKey = Strings.Keys.LastfmApiKey,
                Method = Strings.Methods.GetTracks,
                Limit = 1000,
                Secure = true
            };

            return await Get<GetTracksRequest, GetTracksResponse>(request, cancellationToken);
        }

        public async Task<GetTracksResponse> GetTracks(LastfmUser user, CancellationToken cancellationToken, int page = 0, int limit = 200)
        {
            var request = new GetTracksRequest
            {
                User = user.Username,
                ApiKey = Strings.Keys.LastfmApiKey,
                Method = Strings.Methods.GetTracks,
                Limit = limit,
                Page = page,
                Secure = true

            };

            return await Get<GetTracksRequest, GetTracksResponse>(request, cancellationToken);
        }

        /// <summary>
        /// Renders the request exactly as it is sent to Last.fm, url encoded, with the
        /// credentials removed. The encoding matters: characters that are invisible in a log,
        /// like non breaking spaces or zero width joiners, show up as escape sequences, and
        /// the result can be replayed against the API as is.
        /// </summary>
        private static string DescribeRequest(BaseRequest request)
        {
            var parts = request.ToDictionary()
                .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value))
                .Where(kvp => kvp.Key != "api_key" && kvp.Key != "sk" && kvp.Key != "api_sig")
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => string.Format("{0}={1}", kvp.Key, Uri.EscapeDataString(kvp.Value)));

            return string.Join("&", parts);
        }

        /// <summary>
        /// Last.fm leaves the ignoredMessage text empty in some responses, so fall back to
        /// the documented meaning of the code.
        /// </summary>
        private static string DescribeIgnoredScrobble(IgnoredMessage ignoredMessage)
        {
            if (!string.IsNullOrWhiteSpace(ignoredMessage?.Text))
            {
                return ignoredMessage.Text;
            }

            // https://www.last.fm/api/show/track.scrobble
            return ignoredMessage?.Code switch
            {
                1 => "Artist was ignored",
                2 => "Track was ignored",
                3 => "Timestamp was too old",
                4 => "Timestamp was too new",
                5 => "Daily scrobble limit exceeded",
                _ => "Unknown reason"
            };
        }

        /// <summary>
        /// Checks for duplicate scrobble and updates cache if not duplicate.
        /// Even though MemoryCache is thread-safe, we use the _scrobbleLock to ensure thread safety for the whole check-and-set operation.
        /// The method also updates the cache with the new scrobble if it's not a duplicate.
        /// Returns true if duplicate, false otherwise.
        /// </summary>
        private bool CheckAndUpdateScrobbleCache(string username, string trackId)
        {
            var cacheKey = $"{username}:{trackId}";
            lock (_scrobbleLock)
            {
                if (_scrobbleCache.TryGetValue(cacheKey, out _))
                {
                    _logger.LogInformation("Duplicate scrobble detected for user={0}, trackId={1} within {2} seconds. Skipping.", username, trackId, DuplicateScrobbleTTL.TotalSeconds);
                    return true;
                }
                _scrobbleCache.Set(cacheKey, true, DuplicateScrobbleTTL);
                return false;
            }
        }
    }
}
