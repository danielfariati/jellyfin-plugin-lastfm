## jellyfin-plugin-lastfm

Enables audio scrobbling to Last.fm as well as a metadata fetcher source.

This repository continues the work of the original Jellyfin Last.fm plugin maintained by [jesseward](https://github.com/jesseward/jellyfin-plugin-lastfm), which has since been archived.

The plugin was originally migrated from the Emby repository and adapted to function within the Jellyfin ecosystem.

## 🔧 Installation and Configuration

Install the plugin via the Jellyfin plugin repository. Navigate to the **Plugins** section of the admin dashboard and add the following repository to receive stable builds of this plugin:

- **Repo name:** Last.fm Stable  
- **Repo URL:** https://raw.githubusercontent.com/danielfariati/jellyfin-plugin-lastfm/refs/heads/master/manifest.json

Restart the Jellyfin server after installation.

If you are migrating from the archived jesseward plugin, please read the ["Migrating from the Archived jesseward Plugin"](#-migrating-from-the-archived-jesseward-plugin) section.


## 👤 Per-user Settings

The plugin is configured **per Jellyfin user**.

Select the Jellyfin user from the dropdown at the top of the configuration screen.

When configuring a user, you must provide your **Last.fm username and password once**. The password is **not stored**.

It is used only to authenticate with Last.fm and obtain a **session key**, which is then saved and used for all future scrobbling and API requests. After saving, the password field may display a random-looking value. This is expected and represents the stored **Last.fm session key**, not your plaintext password.

If a user changes their Last.fm password, you may need to reconfigure the plugin for that user.

- **Enable Scrobbling for this user?**  
  Enables or disables Last.fm scrobbling for the selected Jellyfin user.

- **Sync favourites for this user?**  
  Enables two-way synchronization between Jellyfin favourites and Last.fm loved tracks.

- **Use alternative mode and scrobble on `UserDataSaved` events instead of `PlaybackStopped`?**

  By default, the plugin scrobbles tracks when Jellyfin emits the `PlaybackStopped` event. This event is reported by the client, and its timing and accuracy depend on the client implementation. Some clients may emit this event with delayed or synthetic timing, or may not emit it consistently (particularly mobile clients), which can lead to missing or inconsistent scrobbles.
  
  When **Alternative Mode** is enabled, the plugin scrobbles tracks on `UserDataSaved` events instead. These events are triggered when Jellyfin persists playback progress or marks an item as played, making scrobbling dependent on server-side playback state rather than client-reported stop events.

  **Enable Alternative Mode if:**
  - You experience missing or inconsistent scrobbles;
  - You primarily use mobile clients, or clients with unreliable stop reporting;

  **Disable it if:**
  - Your clients reliably report `PlaybackStopped` events;
  - You prefer scrobbling to be triggered by the client-reported stop event rather than by Jellyfin saving user playback data;

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

- Missing scrobbles? Try enabling **Alternative Mode**
- If authentication appears broken, re-enter your Last.fm credentials and save to generate a new session key
- Check Jellyfin server logs for plugin-related messages
- Issues after migrating from the old plugin? Follow the [clean migration steps above](#-migrating-from-the-archived-jesseward-plugin)
