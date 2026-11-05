# Jellyfin.Plugin.Lastfm tests

Run with the .NET 10 SDK:

```sh
cd tests/Jellyfin.Plugin.Lastfm.Tests
dotnet test -c Release
```

The current xUnit tests exercise the plugin's actual playback handlers and API client with a fake HTTP handler. Their 17 assertions cover repeated stop events after a long pause, intentional repeats, late stops crossing repeat boundaries, repeated start events, independent sessions, eligibility thresholds, missing start events, concurrent submissions, failed-request retries, and alternative mode. No requests reach Last.fm.

The long-pause assertion fails against the unmodified `10.11.0.13` release. The fix retains each playback's start timestamp and keys duplicate detection on that identity, so a new playback start allows the same track to scrobble again. State is held in memory for 24 hours and resets when Jellyfin restarts.
