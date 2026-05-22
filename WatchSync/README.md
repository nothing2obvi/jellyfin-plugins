# WatchSync

<p>
  <a href="https://ko-fi.com/yeahnoforsure_" target="_blank" rel="noopener noreferrer"><img src="../assets/support_me_on_kofi_blue.png" alt="Support me on Ko-fi" width="240" /></a>
</p>

Jellyfin plugin to automatically synchronize watch history between libraries of different qualities, such as 4K and HD libraries.

<p align="center">
    <img src="https://github.com/Atilil/jellyfin-plugins/raw/main/WatchSync/Jellyfin.Plugin.WatchSync/WatchSync.png" />
</p>

> **Fork status:** This repository is a fork of [Atilil/jellyfin-plugins](https://github.com/Atilil/jellyfin-plugins), but WatchSync has not been touched yet. If you only want WatchSync, it is best to use the original Atilil repository.

> **Disclaimer:** The original plugins in [Atilil/jellyfin-plugins](https://github.com/Atilil/jellyfin-plugins) are described by their author as built from vibecoding; see [Atilil/jellyfin-plugins#17](https://github.com/Atilil/jellyfin-plugins/issues/17). WatchSync is currently unchanged in this fork.

## Features

- **Automatic synchronization**: When media is watched, all matching versions are updated
- **Smart identification**: Uses IMDB, TMDB and TVDB to match media
- **Full support**: Movies and TV series episodes
- **Granular configuration**:
  - Customizable completion threshold
  - Choice of data to sync, including status, position, play count, and date
  - Exclude specific libraries or users
- **Full sync task**: Scheduled task to synchronize all history

## Installation

See the [main repository README](../README.md) for installation via Jellyfin plugin repository.

> **Warning:** It is recommended to back up your Jellyfin database before installing and using this plugin. The plugin modifies user watch data, and a backup ensures you can restore your data if needed.

## Configuration

After installation, configure the plugin in: **Administration -> Plugins -> WatchSync**

### Media Identification

The plugin uses external identifiers in this priority order:

1. **IMDB ID**
2. **TMDB ID**
3. **TVDB ID**
4. **Title + Year** fallback, disabled by default

## Building from Source

```bash
cd WatchSync
./build.sh
```

Or manually:

```bash
cd Jellyfin.Plugin.WatchSync
dotnet build -c Release
```

The DLL will be generated in `bin/Release/net9.0/`.

## How It Works

The plugin listens to the `PlaybackStopped` event via `ISessionManager`. When media playback ends:

1. Checks if the plugin is enabled and user is not excluded
2. Calculates completion percentage
3. Searches for matching media via `MediaMatcher`
4. Resolves data to sync via `ConflictResolver`
5. Updates `UserData` for matching media

## Architecture

```text
Jellyfin.Plugin.WatchSync/
├── Plugin.cs                    # Entry point
├── PluginServiceRegistrator.cs  # Dependency injection
├── Configuration/
│   ├── PluginConfiguration.cs   # Configuration model
│   └── configPage.html          # Admin interface
├── Services/
│   ├── WatchSyncService.cs      # Main service
│   ├── MediaMatcher.cs          # Matching logic
│   └── ConflictResolver.cs      # Data resolution
├── Tasks/
│   └── FullSyncTask.cs          # Scheduled task
└── Utils/
    └── TitleUtils.cs            # Title normalization
```

---

*This plugin was developed with the assistance of AI (Claude by Anthropic).*
