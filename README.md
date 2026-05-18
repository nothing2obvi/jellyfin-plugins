# nothing2obvi Jellyfin Plugins

This repository is a fork of [Atilil/jellyfin-plugins](https://github.com/Atilil/jellyfin-plugins).

The main active change in this fork is **JellyTag-Plus**, a renamed and expanded fork of JellyTag. **WatchSync has not been touched yet**, so if you only want WatchSync, it is best to use the original repository from Atilil.

## Installation

1. Open Jellyfin and go to **Administration -> Dashboard -> Plugins -> Repositories**
2. Click **Add** and enter:
   - **Name:** `nothing2obvi Plugins`
   - **URL:** `https://raw.githubusercontent.com/nothing2obvi/jellyfin-plugins/main/manifest.json`
3. Click **Save**
4. Go to **Catalog** and install the plugin you want
5. Restart Jellyfin

## Available Plugins

### WatchSync

<p align="center">
    <img src="WatchSync/Jellyfin.Plugin.WatchSync/WatchSync.png" />
</p>

WatchSync automatically synchronizes watch history between libraries of different qualities, such as 4K and HD libraries.

**Fork status:** WatchSync is currently unchanged from the original plugin in [Atilil/jellyfin-plugins](https://github.com/Atilil/jellyfin-plugins). For WatchSync, use the original repository unless you specifically want everything from this fork in one plugin repository.

[More details](WatchSync/README.md)

---

### JellyTag-Plus

<p align="center">
    <img src="Jellytag/JellyTag-Plus.png" alt="JellyTag-Plus" />
</p>

JellyTag-Plus overlays quality, language, and collection badges on Jellyfin posters and thumbnails. It builds on JellyTag with collection badge rules, expanded language flag behavior, force image refresh, per-library badge controls, and an aggressive cache warmer for common client image sizes.

**Highlights:**

- Resolution, HDR, codec, audio, language, VOST, and collection badges
- Multiple collection badge rules with custom uploaded images
- Per-library badge category controls
- Filipino language mapping for `tgl` and `fil` to `flag-fil.svg`
- Undetermined language fallback using `flag-und.svg`
- Alphabetized language flags
- Force image refresh to help clients notice changed artwork
- Cache warmer that pre-renders common image variants for Jellyfin Web, Android/iOS/Desktop Qt web-shell clients, Android TV, Roku, Streamyfin, and Findroid

[More details](Jellytag/README.md)

---

## Requirements

- Jellyfin 10.11.0 or higher
- .NET 9 SDK, if building from source

## License

MIT License - see [LICENSE](LICENSE) file.

## Author

**nothing2obvi**

## Disclaimer

This project includes code derived from the original Jellyfin plugin repository and local modifications for JellyTag-Plus.
