namespace Jellyfin.Plugin.Lastfm
{
    using Api;
    using MediaBrowser.Controller.Entities.Audio;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Session;
    using MediaBrowser.Model.Entities;
    using System.Linq;
    using System.Net.Http;
    using System.Threading.Tasks;
    using System;
    using Microsoft.Extensions.Caching.Memory;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Hosting;
    using System.Threading;

    /// <summary>
    /// Class ServerEntryPoint
    /// </summary>
    public class ServerEntryPoint : IHostedService, IDisposable
    {

        // if the length of the song is >= 30 seconds, allow scrobble.
        private const long minimumSongLengthToScrobbleInTicks = 30 * TimeSpan.TicksPerSecond;
        // if a song reaches >= 4 minutes  in playtime, allow scrobble.
        private const long minimumPlayTimeToScrobbleInTicks = 4 * TimeSpan.TicksPerMinute;
        // if a song reaches >= 50% played, allow scrobble.
        private const double minimumPlayPercentage = 50.00;

        // Cache playback start times so scrobbles can report when the track actually
        // started playing (as required by the Last.fm API) instead of submission time.
        // 24h is well beyond any realistic single-track playback while still bounding the cache.
        private static readonly TimeSpan PlaybackStartTimeTtl = TimeSpan.FromHours(24);

        private readonly ISessionManager _sessionManager;
        private readonly IUserDataManager _userDataManager;
        private readonly MemoryCache _playbackStartTimes = new(new MemoryCacheOptions());

        private LastfmApiClient _apiClient;
        private readonly ILogger<ServerEntryPoint> _logger;

        /// <summary>
        /// Gets the instance.
        /// </summary>
        /// <value>The instance.</value>
        public static ServerEntryPoint Instance { get; private set; }

        public ServerEntryPoint(
            ISessionManager sessionManager,
            IHttpClientFactory httpClientFactory,
            ILoggerFactory loggerFactory,
            IUserDataManager userDataManager)
        {
            _logger = loggerFactory.CreateLogger<ServerEntryPoint>();

            _sessionManager = sessionManager;
            _userDataManager = userDataManager;
            _apiClient = new LastfmApiClient(httpClientFactory, _logger);
            Instance = this;
        }

        /// <summary>
        /// Let last fm know when a user favourites or unfavourites a track
        /// </summary>
        async void UserDataSaved(object sender, UserDataSaveEventArgs e)
        {
            // We only care about audio
            if (e.Item is not Audio)
                return;

            var lastfmUser = Utils.UserHelpers.GetUser(e.UserId);
            if (lastfmUser == null)
            {
                _logger.LogDebug("Could not find user");
                return;
            }

            if (string.IsNullOrWhiteSpace(lastfmUser.SessionKey))
            {
                _logger.LogInformation("No session key present, aborting");
                return;
            }

            var item = e.Item as Audio;

            // Dont do if syncing
            if (Plugin.Syncing)
                return;

            if (e.SaveReason.Equals(UserDataSaveReason.UpdateUserRating))
            {
                if (!lastfmUser.Options.SyncFavourites)
                {
                    _logger.LogDebug("{0} does not want to sync liked songs", lastfmUser.Username);
                    return;
                }
                await _apiClient.LoveTrack(item, lastfmUser, e.UserData.IsFavorite).ConfigureAwait(false);
            }

            if (e.SaveReason.Equals(UserDataSaveReason.PlaybackFinished))
            {
                if (!lastfmUser.Options.Scrobble)
                {
                    _logger.LogDebug("{0} does not want to scrobble", lastfmUser.Username);
                    return;
                }
                if (!lastfmUser.Options.AlternativeMode)
                {
                    _logger.LogDebug("{0} does not use AlternativeMode", lastfmUser.Username);
                    return;
                }
                if (string.IsNullOrWhiteSpace(item.Artists.FirstOrDefault()) || string.IsNullOrWhiteSpace(item.Name))
                {
                    _logger.LogInformation("track {0} is missing  artist ({1}) or track name ({2}) metadata. Not submitting", item.Path, item.Artists.FirstOrDefault(), item.Name);
                    return;
                }
                // Alt mode has no playback position to fall back on, so use now if we
                // missed PlaybackStart (e.g. server restarted mid-track).
                var startedAt = ConsumePlaybackStartTime(e.UserId, item.Id, DateTime.UtcNow);
                await _apiClient.Scrobble(item, lastfmUser, startedAt).ConfigureAwait(false);
            }
        }


        /// <summary>
        /// Let last.fm know when a track has finished.
        /// Playback stopped is run when a track is finished.
        /// </summary>
        private async void PlaybackStopped(object sender, PlaybackStopEventArgs e)
        {
            // We only care about audio
            if (e.Item is not Audio)
                return;

            var item = e.Item as Audio;

            if (e.PlaybackPositionTicks == null)
            {
                _logger.LogDebug("Playback ticks for {0} is null", item.Name);
                return;
            }

            // Required checkpoints before scrobbling noted at https://www.last.fm/api/scrobbling#when-is-a-scrobble-a-scrobble .
            // A track should only be scrobbled when the following conditions have been met:
            //   * The track must be longer than 30 seconds.
            //   * And the track has been played for at least half its duration, or for 4 minutes (whichever occurs earlier.)
            // is the track length greater than 30 seconds.
            if (item.RunTimeTicks < minimumSongLengthToScrobbleInTicks)
            {
                _logger.LogDebug("{0} - played {1} ticks which is less minimumSongLengthToScrobbleInTicks ({2}), won't scrobble.", item.Name, item.RunTimeTicks, minimumSongLengthToScrobbleInTicks);
                return;
            }

            // the track must have played the minimum percentage (minimumPlayPercentage = 50%) or played for atleast 4 minutes (minimumPlayTimeToScrobbleInTicks).
            var playPercent = ((double)e.PlaybackPositionTicks / item.RunTimeTicks) * 100;
            if (playPercent < minimumPlayPercentage & e.PlaybackPositionTicks < minimumPlayTimeToScrobbleInTicks)
            {
                _logger.LogDebug("{0} - played {1}%, Last.Fm requires minplayed={2}% . played {3} ticks of minimumPlayTimeToScrobbleInTicks ({4}), won't scrobble", item.Name, playPercent, minimumPlayPercentage, e.PlaybackPositionTicks, minimumPlayTimeToScrobbleInTicks);
                return;
            }

            var user = e.Users.FirstOrDefault();
            if (user == null)
            {
                return;
            }

            var lastfmUser = Utils.UserHelpers.GetUser(user);
            if (lastfmUser == null)
            {
                _logger.LogDebug("Could not find last.fm user");
                return;
            }

            // User doesn't want to scrobble
            if (!lastfmUser.Options.Scrobble)
            {
                _logger.LogDebug("{0} ({1}) does not want to scrobble", user.Username, lastfmUser.Username);
                return;
            }
            if (lastfmUser.Options.AlternativeMode)
            {
                _logger.LogDebug("{0} uses AlternativeMode", lastfmUser.Username);
                return;
            }

            if (string.IsNullOrWhiteSpace(lastfmUser.SessionKey))
            {
                _logger.LogInformation("No session key present, aborting");
                return;
            }

            if (string.IsNullOrWhiteSpace(item.Artists.FirstOrDefault()) || string.IsNullOrWhiteSpace(item.Name))
            {
                _logger.LogInformation("track {0} is missing  artist ({1}) or track name ({2}) metadata. Not submitting", item.Path, item.Artists.FirstOrDefault(), item.Name);
                return;
            }
            // Fall back to (now - playback position) if PlaybackStart wasn't captured
            // (e.g. server restarted mid-track) — strictly better than submission time.
            var fallbackStart = DateTime.UtcNow - TimeSpan.FromTicks(e.PlaybackPositionTicks ?? 0);
            var startedAt = ConsumePlaybackStartTime(user.Id, item.Id, fallbackStart);
            await _apiClient.Scrobble(item, lastfmUser, startedAt).ConfigureAwait(false);
        }

        /// <summary>
        /// Let Last.fm know when a user has started listening to a track
        /// </summary>
        private async void PlaybackStart(object sender, PlaybackProgressEventArgs e)
        {
            // We only care about audio
            if (e.Item is not Audio)
                return;

            var user = e.Users.FirstOrDefault();
            if (user == null)
            {
                return;
            }

            // Record start time before any opt-out checks so it's available even if
            // config changes between PlaybackStart and the eventual scrobble.
            _playbackStartTimes.Set(BuildPlaybackStartKey(user.Id, e.Item.Id), DateTime.UtcNow, PlaybackStartTimeTtl);

            var lastfmUser = Utils.UserHelpers.GetUser(user);
            if (lastfmUser == null)
            {
                _logger.LogDebug("Could not find last.fm user");
                return;
            }

            // User doesn't want to scrobble
            if (!lastfmUser.Options.Scrobble)
            {
                _logger.LogDebug("{0} ({1}) does not want to scrobble", user.Username, lastfmUser.Username);
                return;
            }

            if (string.IsNullOrWhiteSpace(lastfmUser.SessionKey))
            {
                _logger.LogInformation("No session key present, aborting");
                return;
            }

            var item = e.Item as Audio;
            if (string.IsNullOrWhiteSpace(item.Artists.FirstOrDefault()) || string.IsNullOrWhiteSpace(item.Name))
            {
                _logger.LogInformation("track {0} is missing artist ({1}) or track name ({2}) metadata. Not submitting", item.Path, item.Artists.FirstOrDefault(), item.Name);
                return;
            }
            await _apiClient.NowPlaying(item, lastfmUser).ConfigureAwait(false);
        }

        /// <summary>
        /// Runs this instance.
        /// </summary>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            //Bind events
            _sessionManager.PlaybackStart += PlaybackStart;
            _sessionManager.PlaybackStopped += PlaybackStopped;
            _userDataManager.UserDataSaved += UserDataSaved;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            // Unbind events
            _sessionManager.PlaybackStart -= PlaybackStart;
            _sessionManager.PlaybackStopped -= PlaybackStopped;
            _userDataManager.UserDataSaved -= UserDataSaved;

            // Clean up
            _apiClient = null;
            _playbackStartTimes.Dispose();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }

        private static string BuildPlaybackStartKey(Guid userId, Guid itemId)
        {
            return $"{userId:N}:{itemId:N}";
        }

        private DateTime ConsumePlaybackStartTime(Guid userId, Guid itemId, DateTime fallback)
        {
            var key = BuildPlaybackStartKey(userId, itemId);
            if (_playbackStartTimes.TryGetValue(key, out DateTime startedAt))
            {
                _playbackStartTimes.Remove(key);
                return startedAt;
            }
            _logger.LogDebug("No playback start time cached for user={0} item={1}; falling back to {2:o}", userId, itemId, fallback);
            return fallback;
        }
    }
}
