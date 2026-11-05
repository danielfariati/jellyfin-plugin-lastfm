using System.Net;
using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Lastfm;
using Jellyfin.Plugin.Lastfm.Api;
using Jellyfin.Plugin.Lastfm.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Lastfm.Tests;

/// <summary>
/// Protects scrobbling behavior against previously identified edge cases.
/// </summary>
public sealed class ScrobbleRegressionTests
{
    [Fact]
    public void ThresholdThenLongPauseSubmitsOnlyOnceWithPlaybackStartTimestamp()
    {
        var context = new ScrobbleTestContext();
        var track = context.CreateTrack();

        context.Start(track);
        context.Stop(track, track.RunTimeTicks!.Value * 1000); // Feishin's threshold submission.
        context.Clock.UtcNow += TimeSpan.FromMinutes(10); // Pause/idle stop, beyond the old 15s guard.
        context.Stop(track, TimeSpan.FromSeconds(150).Ticks);

        Assert.Equal(1, context.Http.Scrobbles);
        Assert.Contains("timestamp=", Assert.Single(context.Http.Bodies));
    }

    [Fact]
    public void IntentionalRepeatSubmitsASecondListen()
    {
        var context = new ScrobbleTestContext();
        var track = context.CreateTrack();

        context.Start(track, playSessionId: "repeat-1");
        context.Stop(track, track.RunTimeTicks!.Value, playSessionId: "repeat-1");
        context.Start(track, playSessionId: "repeat-2");
        context.Stop(track, track.RunTimeTicks.Value, playSessionId: "repeat-2");

        Assert.Equal(2, context.Http.Scrobbles);
    }

    [Fact]
    public void IndependentDeviceStartsRetainTheirOwnPlaybackIdentity()
    {
        var context = new ScrobbleTestContext();
        var track = context.CreateTrack();

        context.Start(track, "device-a");
        context.Start(track, "device-b");
        context.Stop(track, track.RunTimeTicks!.Value, "device-b");
        context.Stop(track, track.RunTimeTicks.Value, "device-a");

        Assert.Equal(2, context.Http.Scrobbles);
    }

    [Fact]
    public void TrackScrobblesOnceAfterReachingEligibilityThreshold()
    {
        var context = new ScrobbleTestContext();
        var track = context.CreateTrack();

        context.Start(track);
        context.Stop(track, TimeSpan.FromSeconds(50).Ticks);
        Assert.Equal(0, context.Http.Scrobbles);

        context.Stop(track, TimeSpan.FromSeconds(100).Ticks);
        context.Stop(track, track.RunTimeTicks!.Value);
        Assert.Equal(1, context.Http.Scrobbles);
    }

    [Fact]
    public void MissingStartFallbackIdentityIsRetainedAcrossRepeatedStops()
    {
        var context = new ScrobbleTestContext();
        var track = context.CreateTrack();

        context.Stop(track, track.RunTimeTicks!.Value);
        context.Clock.UtcNow += TimeSpan.FromMinutes(10);
        context.Stop(track, TimeSpan.FromSeconds(150).Ticks);

        Assert.Equal(1, context.Http.Scrobbles);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("ignored")]
    [InlineData("invalid")]
    [InlineData("exception")]
    public void FailedSubmissionCanBeRetriedAndAcceptedRetrySuppressesDuplicates(string failure)
    {
        var context = new ScrobbleTestContext();
        var track = context.CreateTrack();

        context.Start(track);
        context.Http.Failure = failure;
        context.Stop(track, track.RunTimeTicks!.Value);
        context.Http.Failure = null;
        context.Stop(track, track.RunTimeTicks.Value);
        context.Stop(track, track.RunTimeTicks.Value);

        Assert.Equal(2, context.Http.Scrobbles);
    }

    [Fact]
    public async Task ConcurrentDuplicateStopsProduceOneInFlightSubmission()
    {
        var context = new ScrobbleTestContext();
        var track = context.CreateTrack();
        context.Http.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedAt = DateTime.UtcNow;

        var inFlight = context.Api.Scrobble(track, context.LastfmUser, startedAt);
        await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => context.Api.Scrobble(track, context.LastfmUser, startedAt)));

        Assert.Equal(1, context.Http.Scrobbles);
        context.Http.Gate.SetResult();
        await inFlight;
    }

    [Fact]
    public void LateStopFromPreviousPlayDoesNotClaimNextRepeat()
    {
        var context = new ScrobbleTestContext();
        var track = context.CreateTrack();

        context.Start(track, playSessionId: "repeat-1");
        context.Stop(track, track.RunTimeTicks!.Value, playSessionId: "repeat-1");
        context.Start(track, playSessionId: "repeat-2");
        context.Stop(track, track.RunTimeTicks.Value, playSessionId: "repeat-1");
        Assert.Equal(1, context.Http.Scrobbles);

        context.Stop(track, track.RunTimeTicks.Value, playSessionId: "repeat-2");
        Assert.Equal(2, context.Http.Scrobbles);
    }

    [Fact]
    public void NewPlaySessionScrobblesWhenPlaybackStartWasMissed()
    {
        var context = new ScrobbleTestContext();
        var track = context.CreateTrack();

        context.Stop(track, track.RunTimeTicks!.Value, playSessionId: "missing-start-1");
        context.Stop(track, track.RunTimeTicks.Value, playSessionId: "missing-start-1");
        context.Stop(track, track.RunTimeTicks.Value, playSessionId: "missing-start-2");

        Assert.Equal(2, context.Http.Scrobbles);
    }

    [Fact]
    public void RepeatedStartForSamePlaySessionPreservesItsIdentity()
    {
        var context = new ScrobbleTestContext();
        var track = context.CreateTrack();

        context.Start(track, playSessionId: "repeated-start");
        context.Stop(track, track.RunTimeTicks!.Value, playSessionId: "repeated-start");
        context.Clock.UtcNow += TimeSpan.FromMinutes(10);
        context.Start(track, playSessionId: "repeated-start");
        context.Stop(track, track.RunTimeTicks.Value, playSessionId: "repeated-start");

        Assert.Equal(1, context.Http.Scrobbles);
    }

    [Fact]
    public void AlternativeModeDoesNotDuplicatePlaybackFinishedEvent()
    {
        var context = new ScrobbleTestContext();
        context.LastfmUser.Options.AlternativeMode = true;
        var track = context.CreateTrack();
        context.Start(track);
        var args = new UserDataSaveEventArgs
        {
            Item = track,
            UserId = context.User.Id,
            SaveReason = MediaBrowser.Model.Entities.UserDataSaveReason.PlaybackFinished
        };

        context.Invoke("UserDataSaved", args);
        context.Clock.UtcNow += TimeSpan.FromMinutes(10);
        context.Invoke("UserDataSaved", args);

        Assert.Equal(1, context.Http.Scrobbles);
    }
}

internal sealed class ScrobbleTestContext
{
    private readonly ServerEntryPoint _entry;

    public ScrobbleTestContext()
    {
        var paths = DispatchProxy.Create<IApplicationPaths, DefaultsProxy>();
        var serializer = DispatchProxy.Create<IXmlSerializer, DefaultsProxy>();
        var plugin = new Plugin(paths, serializer);
        User = new User("test", "test", "test") { Id = Guid.NewGuid() };
        LastfmUser = new LastfmUser
        {
            Username = "test",
            SessionKey = "test",
            MediaBrowserUserId = User.Id,
            Options = new LastFmUserOptions { Scrobble = true }
        };
        plugin.UpdateConfiguration(new Jellyfin.Plugin.Lastfm.Configuration.PluginConfiguration
        {
            LastfmUsers = new[] { LastfmUser }
        });

        Http = new FakeHttp();
        _entry = new ServerEntryPoint(null, new FakeFactory(Http), NullLoggerFactory.Instance, null);
        Api = (LastfmApiClient)typeof(ServerEntryPoint)
            .GetField("_apiClient", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(_entry)!;
        Clock = new FakeClock();
        typeof(LastfmApiClient)
            .GetField("_scrobbleCache", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(Api, new MemoryCache(new MemoryCacheOptions { Clock = Clock }));
    }

    public User User { get; }

    public LastfmUser LastfmUser { get; }

    public FakeHttp Http { get; }

    public LastfmApiClient Api { get; }

    public FakeClock Clock { get; }

    public Audio CreateTrack() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Regression track",
        Artists = new[] { "Regression artist" },
        RunTimeTicks = TimeSpan.FromSeconds(200).Ticks
    };

    public void Start(Audio track, string device = "device-a", string playSessionId = null) =>
        Invoke("PlaybackStart", new PlaybackStartEventArgs
        {
            Item = track,
            Users = new List<User> { User },
            DeviceId = device,
            PlaySessionId = playSessionId,
            Session = new SessionInfo(null, NullLogger.Instance) { Id = device }
        });

    public void Stop(
        Audio track,
        long ticks,
        string device = "device-a",
        string playSessionId = null) =>
        Invoke("PlaybackStopped", new PlaybackStopEventArgs
        {
            Item = track,
            Users = new List<User> { User },
            PlaybackPositionTicks = ticks,
            DeviceId = device,
            PlaySessionId = playSessionId,
            Session = new SessionInfo(null, NullLogger.Instance) { Id = device }
        });

    public void Invoke(string method, object args) =>
        typeof(ServerEntryPoint)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_entry, new[] { null, args });
}

public class DefaultsProxy : DispatchProxy
{
    private static readonly string Root = Path.Combine(
        Path.GetTempPath(),
        "lastfm-regression-" + Guid.NewGuid());

    protected override object Invoke(MethodInfo method, object[] args) =>
        method.ReturnType == typeof(string)
            ? Root
            : method.ReturnType.IsValueType && method.ReturnType != typeof(void)
                ? Activator.CreateInstance(method.ReturnType)
                : null;
}

internal sealed class FakeClock : ISystemClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
}

internal sealed class FakeFactory(FakeHttp handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

internal sealed class FakeHttp : HttpMessageHandler
{
    private int _scrobbles;

    public int Scrobbles => Volatile.Read(ref _scrobbles);

    public string Failure { get; set; }

    public TaskCompletionSource Gate { get; set; }

    public List<string> Bodies { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        if (!body.Contains("method=track.scrobble", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }

        Interlocked.Increment(ref _scrobbles);
        Bodies.Add(body);
        if (Gate is not null)
        {
            await Gate.Task;
        }

        if (Failure == "exception")
        {
            throw new HttpRequestException("Simulated transport error");
        }

        var json = Failure switch
        {
            "error" => "{\"error\":11,\"message\":\"Simulated service error\"}",
            "ignored" => "{\"scrobbles\":{\"@attr\":{\"accepted\":0,\"ignored\":1}}}",
            "invalid" => "not json",
            _ => "{\"scrobbles\":{\"@attr\":{\"accepted\":1,\"ignored\":0}}}"
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
    }
}
