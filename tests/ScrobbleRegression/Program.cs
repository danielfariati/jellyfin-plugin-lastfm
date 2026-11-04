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

var paths = DispatchProxy.Create<IApplicationPaths, DefaultsProxy>();
var serializer = DispatchProxy.Create<IXmlSerializer, DefaultsProxy>();
var plugin = new Plugin(paths, serializer);
var user = new User("test", "test", "test") { Id = Guid.NewGuid() };
var lastfmUser = new LastfmUser { Username = "test", SessionKey = "test",
    MediaBrowserUserId = user.Id, Options = new LastFmUserOptions { Scrobble = true } };
plugin.UpdateConfiguration(new Jellyfin.Plugin.Lastfm.Configuration.PluginConfiguration
    { LastfmUsers = new[] { lastfmUser } });

Audio Track() => new() { Id = Guid.NewGuid(), Name = "Regression track",
    Artists = new[] { "Regression artist" }, RunTimeTicks = TimeSpan.FromSeconds(200).Ticks };
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    Console.WriteLine("PASS: " + name);
}
void Invoke(ServerEntryPoint entry, string method, object args) =>
    typeof(ServerEntryPoint).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
        .Invoke(entry, new object[] { null, args });
void Start(ServerEntryPoint entry, Audio track, string device = "device-a", string playSessionId = null) => Invoke(entry,
    "PlaybackStart", new PlaybackStartEventArgs { Item = track, Users = new List<User> { user }, DeviceId = device,
        PlaySessionId = playSessionId, Session = new SessionInfo(null, NullLogger.Instance) { Id = device } });
void Stop(ServerEntryPoint entry, Audio track, long ticks, string device = "device-a", string playSessionId = null) => Invoke(entry,
    "PlaybackStopped", new PlaybackStopEventArgs { Item = track, Users = new List<User> { user },
        PlaybackPositionTicks = ticks, DeviceId = device, PlaySessionId = playSessionId,
        Session = new SessionInfo(null, NullLogger.Instance) { Id = device } });

var http = new FakeHttp();
var factory = new FakeFactory(http);
var entry = new ServerEntryPoint(null, factory, NullLoggerFactory.Instance, null);
var api = (LastfmApiClient)typeof(ServerEntryPoint)
    .GetField("_apiClient", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(entry);
var clock = new FakeClock();
typeof(LastfmApiClient).GetField("_scrobbleCache", BindingFlags.Instance | BindingFlags.NonPublic)
    .SetValue(api, new MemoryCache(new MemoryCacheOptions { Clock = clock }));

var track = Track();
Start(entry, track);
Stop(entry, track, track.RunTimeTicks.Value * 1000); // Feishin's threshold submission.
clock.UtcNow += TimeSpan.FromMinutes(10); // Pause/idle stop, beyond the old 15s guard.
Stop(entry, track, TimeSpan.FromSeconds(150).Ticks);
Check(http.Scrobbles == 1, "threshold -> long pause -> resume/idle stop submits once");
Check(http.Bodies.Single().Contains("timestamp="), "submission includes the playback-start timestamp");

// A real start resets the same track, even inside the former 15-second window.
Start(entry, track);
Stop(entry, track, track.RunTimeTicks.Value);
Check(http.Scrobbles == 2, "intentional repeat submits a second listen");

Start(entry, track, "device-b");
Stop(entry, track, track.RunTimeTicks.Value, "device-b");
Stop(entry, track, track.RunTimeTicks.Value, "device-a");
Check(http.Scrobbles == 3, "independent device starts do not replace another device's identity");

var beforeThreshold = Track();
Start(entry, beforeThreshold);
Stop(entry, beforeThreshold, TimeSpan.FromSeconds(50).Ticks);
Check(http.Scrobbles == 3, "stop below eligibility threshold does not scrobble");
Stop(entry, beforeThreshold, TimeSpan.FromSeconds(100).Ticks);
Stop(entry, beforeThreshold, beforeThreshold.RunTimeTicks.Value);
Check(http.Scrobbles == 4, "eligible idle stop before Feishin threshold still submits once");

var missingStart = Track();
Stop(entry, missingStart, missingStart.RunTimeTicks.Value);
clock.UtcNow += TimeSpan.FromMinutes(10);
Stop(entry, missingStart, TimeSpan.FromSeconds(150).Ticks);
Check(http.Scrobbles == 5, "missing-start fallback identity is retained across repeated stops");

foreach (var failure in new[] { "error", "ignored", "invalid", "exception" })
{
    var retry = Track();
    Start(entry, retry);
    http.Failure = failure;
    var before = http.Scrobbles;
    Stop(entry, retry, retry.RunTimeTicks.Value);
    http.Failure = null;
    Stop(entry, retry, retry.RunTimeTicks.Value);
    Stop(entry, retry, retry.RunTimeTicks.Value);
    Check(http.Scrobbles == before + 2, failure + " permits retry; accepted retry suppresses later duplicates");
}

var concurrent = Track();
http.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var startedAt = DateTime.UtcNow;
var beforeConcurrent = http.Scrobbles;
var inFlight = api.Scrobble(concurrent, lastfmUser, startedAt);
await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => api.Scrobble(concurrent, lastfmUser, startedAt)));
Check(http.Scrobbles == beforeConcurrent + 1, "concurrent duplicate stops produce one in-flight HTTP submission");
http.Gate.SetResult();
await inFlight;
http.Gate = null;

var repeatBoundary = Track();
var repeatBoundaryBefore = http.Scrobbles;
Start(entry, repeatBoundary, playSessionId: "repeat-1");
Stop(entry, repeatBoundary, repeatBoundary.RunTimeTicks.Value, playSessionId: "repeat-1");
Start(entry, repeatBoundary, playSessionId: "repeat-2");
Stop(entry, repeatBoundary, repeatBoundary.RunTimeTicks.Value, playSessionId: "repeat-1");
Check(http.Scrobbles == repeatBoundaryBefore + 1,
    "late stop from the previous play does not claim the next repeat");
Stop(entry, repeatBoundary, repeatBoundary.RunTimeTicks.Value, playSessionId: "repeat-2");
Check(http.Scrobbles == repeatBoundaryBefore + 2,
    "next repeat scrobbles when its own play session becomes eligible");

var missingStartRepeat = Track();
var missingStartRepeatBefore = http.Scrobbles;
Stop(entry, missingStartRepeat, missingStartRepeat.RunTimeTicks.Value, playSessionId: "missing-start-1");
Stop(entry, missingStartRepeat, missingStartRepeat.RunTimeTicks.Value, playSessionId: "missing-start-1");
Stop(entry, missingStartRepeat, missingStartRepeat.RunTimeTicks.Value, playSessionId: "missing-start-2");
Check(http.Scrobbles == missingStartRepeatBefore + 2,
    "a new play session scrobbles even when its playback start was missed");

var repeatedStart = Track();
var repeatedStartBefore = http.Scrobbles;
Start(entry, repeatedStart, playSessionId: "repeated-start");
Stop(entry, repeatedStart, repeatedStart.RunTimeTicks.Value, playSessionId: "repeated-start");
clock.UtcNow += TimeSpan.FromMinutes(10);
Start(entry, repeatedStart, playSessionId: "repeated-start");
Stop(entry, repeatedStart, repeatedStart.RunTimeTicks.Value, playSessionId: "repeated-start");
Check(http.Scrobbles == repeatedStartBefore + 1,
    "repeated start for the same play session preserves its identity");

// Alternative mode uses user-data events, which share the retained fallback identity.
lastfmUser.Options.AlternativeMode = true;
var alt = Track();
Start(entry, alt);
var altBefore = http.Scrobbles;
var altArgs = new UserDataSaveEventArgs { Item = alt, UserId = user.Id,
    SaveReason = MediaBrowser.Model.Entities.UserDataSaveReason.PlaybackFinished };
Invoke(entry, "UserDataSaved", altArgs);
clock.UtcNow += TimeSpan.FromMinutes(10);
Invoke(entry, "UserDataSaved", altArgs);
Check(http.Scrobbles == altBefore + 1, "alternative mode does not duplicate a PlaybackFinished event");
Console.WriteLine("All 17 regression checks passed. No real HTTP requests were sent.");

public class DefaultsProxy : DispatchProxy
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "lastfm-regression-" + Guid.NewGuid());
    protected override object Invoke(MethodInfo method, object[] args) =>
        method.ReturnType == typeof(string) ? Root :
        method.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null;
}
sealed class FakeClock : ISystemClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
}
sealed class FakeFactory(FakeHttp handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
sealed class FakeHttp : HttpMessageHandler
{
    public int Scrobbles;
    public string Failure;
    public TaskCompletionSource Gate;
    public List<string> Bodies = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var body = await request.Content.ReadAsStringAsync(token);
        if (!body.Contains("method=track.scrobble"))
            return new(HttpStatusCode.OK) { Content = new StringContent("{}") };
        Interlocked.Increment(ref Scrobbles);
        Bodies.Add(body);
        if (Gate != null) await Gate.Task;
        if (Failure == "exception") throw new HttpRequestException("Simulated transport error");
        var json = Failure switch
        {
            "error" => "{\"error\":11,\"message\":\"Simulated service error\"}",
            "ignored" => "{\"scrobbles\":{\"@attr\":{\"accepted\":0,\"ignored\":1}}}",
            "invalid" => "not json",
            _ => "{\"scrobbles\":{\"@attr\":{\"accepted\":1,\"ignored\":0}}}"
        };
        return new(HttpStatusCode.OK) { Content = new StringContent(json) };
    }
}
