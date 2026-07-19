## jellyfin-plugin-lastfm

Enables audio scrobbling to Last.fm as well as a metadata fetcher source.

This repository continues the work of the original Jellyfin Last.fm plugin maintained by [jesseward](https://github.com/jesseward/jellyfin-plugin-lastfm), which has since been archived.

The plugin was originally migrated from the Emby repository and adapted to function within the Jellyfin ecosystem.

## 🔧 Installation and Configuration

### Requirements

The plugin requires **Jellyfin 10.11 or newer**. On older servers it will not show up in the plugin catalog at all - the repository will look empty, because no version of the plugin is compatible with them.

The latest updates additionally require **Jellyfin 10.11.9 or newer**. If your server is older than that, the catalog will keep offering you the last version that is compatible with it, instead of the newest one.

Neither case means the repository is broken. Update Jellyfin to receive newer plugin versions.

### Installing

Install the plugin via the Jellyfin plugin repository. Navigate to the **Plugins** section of the admin dashboard and add the following repository to receive stable builds of this plugin:

- **Repo name:** Last.fm Stable  
- **Repo URL:** https://raw.githubusercontent.com/danielfariati/jellyfin-plugin-lastfm/refs/heads/master/manifest.json

Restart the Jellyfin server after installation.

If you are migrating from the archived jesseward plugin, please read the ["Migrating from the Archived jesseward Plugin"](#-migrating-from-the-archived-jesseward-plugin) section.


## 👤 Per-user Settings

The plugin is configured **per Jellyfin user**.

Select the Jellyfin user from the dropdown at the top of the configuration screen.

When configuring a user, you must provide your **Last.fm username and password once**. The password is **not stored**.

It is used only to authenticate with Last.fm and obtain a **session key**, which is then saved and used for all future scrobbling and API requests.

If a user changes their Last.fm password, you may need to reconfigure the plugin for that user.

- **Enable Scrobbling for this user?**  
  Enables or disables Last.fm scrobbling for the selected Jellyfin user.

- **Sync favourites for this user?**  
  Enables two-way synchronization between Jellyfin favourites and Last.fm loved tracks.

- **Use alternative mode and scrobble on `UserDataSaved` events instead of `PlaybackStopped`?**

  By default, the plugin scrobbles tracks when Jellyfin emits the `PlaybackStopped` event. This event is reported by the client, and its timing and accuracy depend on the client implementation. Some clients may emit this event with delayed or synthetic timing, or may not emit it consistently (particularly mobile clients), which can lead to missing or inconsistent scrobbles.
  
  When **Alternative Mode** is enabled, the plugin scrobbles tracks on `UserDataSaved` events instead - specifically when Jellyfin **marks the track as played**. This makes scrobbling depend on server-side playback state rather than on the client-reported stop event.

  **Enable Alternative Mode if:**
  - You experience missing or inconsistent scrobbles;
  - You primarily use mobile clients, or clients with unreliable stop reporting;

  **Disable it if:**
  - Your clients reliably report `PlaybackStopped` events;
  - You prefer scrobbling to be triggered by the client-reported stop event rather than by Jellyfin saving user playback data;

  ⚠️ **Alternative Mode is not a fix for every client.** It still depends on the client reporting playback well enough for Jellyfin to mark the track as played. If a client never gets that far, neither mode will scrobble, and the track will only ever show as "Scrobbling now" on Last.fm without ever landing in your history. In that case the problem is in the client itself and has to be reported to that client's developers.

- **Advanced options**
  - **API host:**

    Allows you to specify a custom API host for Last.fm-compatible services / APIs.
    For example:
    - **Last.fm (Default):** `ws.audioscrobbler.com`
    - **Libre.fm:** `libre.fm`

    If you change this value, you have to re-enter the password and save the configuration for the change to take effect, as the session key is tied to the API host.

    Please note that while the plugin may work with Last.fm-compatible services, it is primarily designed and tested against the official Last.fm API. Compatibility with other services may vary based on how closely they adhere to the Last.fm API specifications.

## 🔄 Migrating from the Archived jesseward Plugin

This plugin replaces and continues the [archived repository](https://github.com/jesseward/jellyfin-plugin-lastfm).

If you are migrating from the old plugin, a **clean installation is strongly recommended** to avoid configuration conflicts or stale plugin data.

**Recommended migration steps:**
1. Uninstall the existing Last.fm plugin
2. Remove the old plugin repository
3. Restart the Jellyfin server
4. Add this repository
5. Install the plugin [from the new repository](#-installation-and-configuration)
6. Restart Jellyfin server once more
7. Reconfigure user credentials

While some setups may continue working without a clean install, performing these steps ensures a reliable and predictable migration.

## 🛠 Troubleshooting

- Missing scrobbles? Try enabling **Alternative Mode**, keeping its limitations in mind (more details in the [Per-user Settings](#-per-user-settings) section)
- If authentication appears broken, re-enter your Last.fm credentials and save to generate a new session key
- If using a custom API host (for example Libre.fm), confirm the host is correct and then re-authenticate to refresh the session key for that host
- Check the Jellyfin server logs - the plugin logs why a track was or wasn't scrobbled (see [Reporting an issue](#reporting-an-issue))
- Not being offered the latest plugin version? Check the [Requirements](#requirements) section - newer versions need a newer Jellyfin server
- Issues after migrating from the old plugin? Follow the [clean migration steps above](#-migrating-from-the-archived-jesseward-plugin)

### Reporting an issue

**Please update to the [latest release](https://github.com/danielfariati/jellyfin-plugin-lastfm/releases/latest) and confirm the problem still happens there before opening an issue.** It may already be fixed, and older versions logged far less information, which makes them much harder to debug.

The plugin logs what it is doing, which usually explains a missing scrobble on its own. When opening an issue, please include these lines from the Jellyfin server log:

| Log line | What it tells us |
|---|---|
| `Last.fm plugin v...` | Which plugin version is actually loaded. Only printed on server startup, so restart Jellyfin if you cannot find it |
| `UserDataSaved: ...` | Which playback events Jellyfin received, and their reason. Only relevant to **Alternative Mode**, which scrobbles when a `PlaybackFinished` event arrives - if that one never shows up for a track, the client is not reporting playback well enough for Jellyfin to mark it as played |
| `PlaybackStopped: ...` | The stop event arrived, with how much of the track was played. Only relevant to the **default mode**. If it never appears for a track, the client is not reporting stops |
| `... won't scrobble` | The track was deliberately skipped, and why - usually too short, or not played far enough |
| `... Not submitting` | The track was skipped because it is missing artist or track name metadata |
| `No session key present, aborting` | That user is not authenticated with Last.fm. Re-enter the credentials and save |
| `Submitting scrobble: ...` | A scrobble was sent, including the timestamp used |
| `Scrobble succeeded: ...` | Last.fm accepted the scrobble |
| `Scrobble ignored by Last.fm: ...` | Last.fm received the scrobble but discarded it, including the reason why |
| `Scrobble failed ...` / `Scrobble exception: ...` | The request itself failed, including the error returned |

Please also include your Jellyfin version, plugin version, and which client you were playing from, since a lot of scrobbling problems turn out to be client-specific.
