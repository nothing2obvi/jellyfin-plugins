# JellyTag-Plus

<p>
  <a href="https://ko-fi.com/yeahnoforsure_" target="_blank" rel="noopener noreferrer"><img src="../assets/support_me_on_kofi_blue.png" alt="Support me on Ko-fi" width="240" /></a>
</p>

JellyTag-Plus overlays quality, language, and collection badges on Jellyfin posters and thumbnails. It builds on the original plugin from [Atilil/jellyfin-plugins](https://github.com/Atilil/jellyfin-plugins) and uses a separate name so both projects can coexist.

> **Disclaimer:** The original plugins in [Atilil/jellyfin-plugins](https://github.com/Atilil/jellyfin-plugins) are described by their author as built from vibecoding; see [Atilil/jellyfin-plugins#17](https://github.com/Atilil/jellyfin-plugins/issues/17). The additional and modified features in JellyTag-Plus are also vibecoded.

<p align="center">
    <img src="JellyTag-Plus.png" alt="JellyTag-Plus" width="60%" />
</p>

## Features

- Resolution, HDR, video codec, audio, language, VOST, and collection badges
- Server-side overlays for posters and thumbnails
- Per-library badge categories and per-target controls for posters, season posters, series/movie thumbnails, episode thumbnails, videos, and other supported images
- Configurable panel position, order, layout, size, spacing, and image or text style
- Regex-based collection rules with custom images and placement
- Alphabetized language flags, with series-level aggregation across episodes
- Filipino flags for `tgl` and `fil`; `flag-und.svg` for missing or unknown languages
- Config export/import, image caching, and scheduled cleanup and warmup tasks

## Installation and compatibility

1. Open **Dashboard → Plugins → Repositories** in Jellyfin.
2. Add this repository URL:
   ```text
   https://raw.githubusercontent.com/nothing2obvi/jellyfin-plugins/main/manifest.json
   ```
3. Find **JellyTag-Plus** in **Catalog** and install it.
4. Restart Jellyfin.

| Jellyfin | Plugin release line | Target ABI | Runtime |
|----------|---------------------|------------|---------|
| 10.11.x | 1.51.x (current: 1.51.44.0) | `10.11.0.0` | .NET 9 |
| 12 | 1.52.x (current: 1.52.40.0) | `12.0.0.0` | .NET 10 |

The runtime comes with Jellyfin. The Jellyfin 12 package uses `12.0.0-rc3` API packages and supports rc2/rc3. Jellyfin can update from the 1.51.x plugin line to 1.52.x when you upgrade the server.

Existing cached images can be reused across a Jellyfin upgrade when the source image, request parameters, badge state, and plugin settings still match.

## Configuration

Open **Dashboard → Plugins → JellyTag-Plus**.

### Global settings

| Option | Description | Default |
|--------|-------------|---------|
| Enable JellyTag-Plus | Enable overlays globally | Enabled |
| Output Format | JPEG or WebP | JPEG |
| JPEG Quality | Output image quality | 90 |
| Cache Duration | Cache lifetime in hours; `0` keeps images indefinitely | 168 |
| Validate Expired Cache Before Re-rendering | Reuse expired images when the source and badges are unchanged. Shortcut cache hits skip this metadata check. | Disabled |
| Normal Render Max Concurrency | Simultaneous uncached renders during browsing; cache hits are not throttled | 2 |
| Force Image Refresh | Help clients fetch updated artwork; see below | Disabled |
| Excluded Libraries | Libraries to skip | None |

A render concurrency of `1` may suit slower servers; higher values may help with uncached pages if CPU and storage have room. Render slots are released before cache writes and client transfers.

### Poster and thumbnail settings

Each image type has independent badge panels. Thumbnails can also mirror poster settings with a size reduction.

| Setting | Description |
|---------|-------------|
| Enabled | Show or hide the category |
| Position | Corner, border-center, or edge placement |
| Layout | Horizontal or vertical stacking |
| Style | Image or text badges |
| Size % | Badge width as a percentage of the image |
| Margin % | Distance from the edge |
| Gap % | Space between badges |
| Display Mode | Highest quality only, or all |
| Order | Panel stacking order |
| Text colors | Background, text color, opacity, and corner radius |

### Collection badges

Rules match **Jellyfin collection titles** using regex, not TMDB collection IDs. Custom collections are supported.

Each rule has a badge key, regex, text label, optional uploaded image, position/layout overrides, and target toggles for posters, season posters, series thumbnails, episode thumbnails, videos, and other images.

Rules use the shared Collection Badges style unless overridden. Matching badges at the same position stack in rule order. Left/top stacks follow normal order; right-aligned horizontal stacks extend left, and bottom-aligned vertical stacks extend upward.

### Custom badges

Replace default badge images or customize labels for text badges. Uploaded assets are stored in the plugin data folder and survive updates.

## Cache warmer

The **JellyTag-Plus Cache Warmer** scheduled task pre-renders poster and thumbnail variants from enabled libraries. Enable, disable, and reorder client profiles on the configuration page.

Fixed profiles cover Findroid, Jellyfin Android TV, Jellyfin Roku, Streamyfin, Wholphin, Moonfin Core, Moonfin tvOS, Moonfin Smart-TV, Moonfin Roku, DUNE, Swiftfin, and Jellium Desktop.

These profiles cover known `Primary` and `Thumb` sizes. Dynamic grid, card, screen, and layout sizes use **Learned Clients**. Jellyfin Web and web-based clients—including Android, iOS, Desktop Qt, Tizen, WebOS, and Xbox—also use Learned Clients rather than fixed profiles.

### Order and learned variants

The warmer runs these phases in order: **Home → Libraries → Home & Libraries → Episodes → Videos → Other**. Within each phase, it follows your client profile order.

**Learned Clients** is enabled and placed last by default. It records real `Primary` and `Thumb` requests, including dimensions and quality, rounds dimensions to the nearest 10 pixels, and skips variants already covered by fixed profiles.

Learned requests use **Home & Libraries** when their source view is unclear, **Episodes** for episodes, and **Videos** for music videos and standalone videos. Moving the profile changes its priority within a phase, not the phase order.

New variants join the next warmer run. Restart an active run to include them immediately. Variants do not expire automatically; use **JellyTag-Plus Clear Learned Client Profile** to remove them.

### Throttling

| Option | Description | Default |
|--------|-------------|---------|
| Warmer Max Concurrency | Simultaneous warmer requests | 1 |
| Warmer Delay | Delay after each request, in milliseconds | 5000 |
| Warmer Client Quiet Window | Seconds without normal image traffic before warmup resumes | 120 |
| Pause warmer during active playback | Wait while any Jellyfin session is playing; paused playback does not count | Disabled |

The default profile order is Jellyfin Android TV, Jellyfin Roku, Streamyfin, Wholphin, Moonfin Core, Moonfin tvOS, Moonfin Smart-TV, Moonfin Roku, DUNE, Swiftfin, Jellium Desktop, Findroid, then Learned Clients.

Normal image requests take priority. The warmer waits for the quiet window after browsing activity before continuing.

> The warmer can create many images per item. Learned Clients has no variant cap, so storage and warmup time can grow substantially. Enable only the profiles you need and watch disk usage.

### Progress

Successful warmup requests are recorded so later runs can resume unfinished work. Each client/phase also checks for matching images cached during normal browsing before sending requests. Clearing the plugin image cache clears warmer progress too.

**Estimated Warmer Progress** shows client and phase percentages, with approximate ETAs when enough recent completions are available. Learned Clients percentages can drop as new variants increase the total work.

Run **JellyTag-Plus Calculate Progress and Totals** to update progress estimates and cache file/size totals. The configuration page displays saved results rather than scanning libraries and cache folders on every visit.

## Badge status and image caching

JellyTag-Plus detects badges from metadata and collection membership, applies your settings, and caches the rendered image. Matching requests reuse the cache.

Run **JellyTag-Plus Build Badge Status Index** after configuring badges or changing collections and rules to precompute badge state. New or changed items can still be checked on demand. Cached overlays are rendered again when their badge state differs from the index.

Learned profiles, image-cache indexes, and badge-status indexes update in memory immediately and save every 30 seconds, with a final flush on normal shutdown. Image files are saved before a render is reported as cached. An abrupt stop may lose recent unsaved index or learning updates; cache shortcuts are relearned on request, and the badge-status task can refresh its index.

After five seconds of browsing/render inactivity, a small completion check retries recent uncached renders that failed to reach the cache, once.

## Force Image Refresh

This optional setting helps clients notice changed badges by briefly swapping and restoring the stored artwork. The badge-status task also refreshes image metadata when badge state changes; its first run after an index upgrade may refresh existing images once.

Unlike normal overlays, this changes stored artwork temporarily. Keep image backups and use it carefully.

## Bypass overlays

Add `jellytag=off` to an image request to get Jellyfin's image without JellyTag-Plus overlays. This is useful for export and archive tools such as [Pixelfin](https://github.com/nothing2obvi/pixelfin).

```text
/Items/{itemId}/Images/Primary?jellytag=off
```

This does not remove badges already embedded in the stored image by another tool. JellyTag-Plus processes only GET requests; uploads and deletions pass directly to Jellyfin.

## Troubleshooting

- **Missing badges:** Check that the plugin, library, badge category, and image target are enabled.
- **Outdated badges:** Run the badge-status index task after changing rules or collections. If the client still shows old artwork, clear its image cache or consider Force Image Refresh.
- **Large cache:** Disable unnecessary warmer profiles, lower cache duration, or clear the plugin cache. Learned variants can be cleared separately.
- **Slow first warmup:** The first run may render many variants. Later runs reuse valid cached images.

## Building and regression checks

From the `Jellytag` directory:

```bash
./build.sh          # Jellyfin 12 / .NET 10
./build.sh 10.11    # Jellyfin 10.11 / .NET 9
```

Use an SDK that supports the target framework; Jellyfin 12 requires the .NET 10 SDK. Set `JELLYFIN_PACKAGE_VERSION` to override the Jellyfin 12 API package version, or `JELLYFIN_SOURCE_ROOT` to build against a local Jellyfin checkout.

Run regression checks with the .NET 10 SDK:

```bash
dotnet run --project tests/JellyTag.RegressionTests.csproj -c Release
```

Checks cover image upload/deletion passthrough, rendering, SVG reuse, and batched persistence, including concurrent updates, clearing, retries, and shutdown.

## License

MIT License
