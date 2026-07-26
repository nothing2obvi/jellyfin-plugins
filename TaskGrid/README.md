# Task Grid

<p>
  <a href="https://ko-fi.com/yeahnoforsure_" target="_blank" rel="noopener noreferrer"><img src="../assets/support_me_on_kofi_blue.png" alt="Support me on Ko-fi" width="240" /></a>
</p>

Task Grid is a Jellyfin plugin that shows scheduled tasks on a Monday-first weekly grid, with days as rows and hours as columns.

<p align="center">
    <img src="TaskGrid.png" alt="Task Grid" width="60%" />
</p>

> **Fork status:** Task Grid is not a fork of [Atilil/jellyfin-plugins](https://github.com/Atilil/jellyfin-plugins). It is a new plugin added in this repository.

## Features

- Monday-through-Sunday task grid, with Monday on top
- 24 hour columns for each day
- Task blocks sized by maximum runtime when Jellyfin exposes one
- Fallback sizing from the last completed runtime, or a short estimate when no duration is available
- Red warning treatment when the most recent task result says it was aborted by shutdown
- Refresh button for recently changed schedules
- Per-task color coding
- Heavy-task flags with overlap warnings
- Optional display of hidden tasks and non-daily/non-weekly trigger tasks

## Notes

Task Grid reads Jellyfin's scheduled task data through the same scheduled task API used by the admin dashboard. It does not change task schedules. Display preferences such as colors and heavy-task flags are stored in the plugin configuration.

Jellyfin does not always know how long a future task will run. When a scheduled trigger has a maximum runtime, Task Grid uses that value. Otherwise it uses the last completed runtime when available, then falls back to a 30 minute display estimate.

## Installation

See the [main repository README](../README.md) for installation via Jellyfin plugin repository once a release has been published.

## Building from Source

```bash
cd TaskGrid
./build.sh
```

Or manually:

```bash
cd TaskGrid/Jellyfin.Plugin.TaskGrid
dotnet build -c Release
```

The DLL will be generated in `bin/Release/net9.0/`.

## Architecture

```text
Jellyfin.Plugin.TaskGrid/
├── Plugin.cs
├── Configuration/
│   ├── PluginConfiguration.cs
│   └── configPage.html
└── Jellyfin.Plugin.TaskGrid.csproj
```

---

*This plugin was developed with the assistance of AI.*
