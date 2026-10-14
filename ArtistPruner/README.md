# Artist Pruner

> **WARNING: IN DEVELOPMENT — DO NOT USE.** Artist Pruner is unfinished and is not safe or supported for use on Jellyfin libraries. Do not install or run it, including dry-run mode. Existing downloads are development artifacts, not releases suitable for use.

See [development status](DEVELOPMENT.md). The feature descriptions and commands below are developer reference only, not instructions or encouragement to use the plugin.

![Artist Pruner](assets/artist-pruner.png)

Preview music-credit discrepancies and optionally apply reversible corrections.
**Development artifact — do not use.** Dry-run and full mode are implemented, but neither is approved for use.
Separate builds support **Jellyfin 10.11.x (.NET 9)** and **12.x (.NET 10)**.

## Modes

- **Dry-run:** probes local tracks in Music libraries, compares track/album credits,
  and logs discrepancies and eligible corrections. No library writes or backups.
- **Full mode:** removes surplus **artist and album-artist credits on tracks and albums**
  when raw embedded tags and Jellyfin's probe agree. Album evidence is the union
  of physical tags across every track; stored credits are the correction target.
  It preserves the primary credit in each field and never adds, renames, or merges names.
  It writes and validates a recovery point before persisting the changes.

Full mode does **not delete artist records, artwork, music files, or NFO files**.
Name replacements, empty results, and unsupported cases remain review-only. Therefore,
retained artist entries may still appear in the Artists view after correction.
This is not a general duplicate-artist deletion tool.

Affected tracks, albums, and retained extra artist records are metadata-locked to prevent
normal refreshes from undoing corrections and native orphan cleanup from deleting
recovery data. Restore reverses those locks. Until restored or manually unlocked,
affected items will not automatically pick up future tag changes. Avoid manual
unlocking/deletion before restoration.

## Installation reference — developers only; do not use

Install Artist Pruner from this repository's plugin catalog and restart Jellyfin,
or extract the matching release ZIP into its own directory under Jellyfin's
plugins folder. Include the bundled **TagLibSharp.dll**; do not copy Jellyfin SDK
assemblies into that directory. Do not install both ABI builds simultaneously.

1. Make an independent Jellyfin server backup before testing full mode.
2. Open **Dashboard → Plugins → Artist Pruner**; keep **Dry-run mode** checked.
3. Run **Prune Music Artists** in **Dashboard → Scheduled Tasks** and review server
   logs for proposed corrections, or click **Show proposed prunes** on the plugin
   page to view the latest run as a table. A discrepancy alone is not an eligible change.
4. To apply eligible corrections, uncheck **Dry-run mode**, save, and rerun the
   task. Full mode re-evaluates the library; it does not apply a saved preview.
5. Refresh the configuration page's recovery list and test **Restore selected
   backup**, starting with the newest unrestored point.

Automatic execution after successful full-library scans is optional and off by
default. Individual folder refreshes do not trigger it. Native recurring task
schedules are supported; none is enabled by default.

The preview table is a structured companion to the same run's logs, not a parser
for historic server logs. It includes item name/ID, field, stored and probed file
credits, proposed removals, and review-only status. Running, failed, cancelled,
and interrupted reports are explicitly labelled; they are not complete plans.
Run the task again after upgrading to populate it. A new run replaces the old
report. Reports and recovery data are administrator-only.

## Recovery and safety limits

Recovery points live in the plugin data directory's `backups` subdirectory and
record original credits, identities, and metadata-lock states. Writes use an
atomic replacement, durable file flush, checksum, and read-back validation.
Prepared and applied points are always retained; retention applies only to
restored points (default five). A pending Prepared point blocks further cleanup
until restored. Restore refuses conflicting credit/identity changes and preserves
unrelated metadata edits. Restore endpoints require administrator elevation.

No direct SQL, entity deletion, file tagging, or metadata-saver calls are used.
Changes are persisted through Jellyfin's public item-persistence service. Recovery
points are not a substitute for a server backup and cannot resurrect externally
deleted entities. Backups from the earlier incomplete implementation are not
compatible and are left untouched.

- Eligible full-mode formats: FLAC, MP3, Ogg, and Opus. Other formats are audit-only.
- Exact case/Unicode comparisons; no transliteration or fuzzy identity matching.
- Plural ARTISTS/ALBUMARTISTS tags are conservatively review-only because they
  can contain collaborators omitted by default tag accessors.
- Missing/malformed tags, parser disagreements, locked albums/tracks, duplicate
  matching artist identities, and changes to the primary artist are skipped.
- The plugin rejects overlapping operations and observed full-library scans.
  **Run full mode and restore while metadata editing/refresh activity is idle.**
  Jellyfin exposes no global transaction covering scans, file edits, and plugin
  planning, so these checks cannot eliminate all concurrent-edit races.
- Music videos and non-Music libraries are excluded. Unowned global artists are
  not treated as proven orphans. Provider/scanner behavior is not fixed upstream.

## Development build and verification

Use a .NET 10 SDK (the test runner targets .NET 10):

```sh
dotnet run --project ArtistPruner/tests -c Release -p:JellyfinTargetAbi=10.11.0.0
dotnet run --project ArtistPruner/tests -c Release -p:JellyfinTargetAbi=12.0.0.0
node ArtistPruner/tests/preview-page.test.cjs
bash ArtistPruner/build.sh all
```

Packages appear in `ArtistPruner/dist` with explicit ABI suffixes. The default
project build targets Jellyfin 12.0.0; the 10.11 build uses SDK 10.11.0.
The regression runner covers audit behavior, real embedded FLAC tags, full-mode
planning/application/restoration, conflicts, cancellation, interrupted writes,
backup corruption, and retention using strict interface fakes. These are **not
live-server integration tests**. Plugin discovery, UI, native persistence, scans,
and additional real-world tagged files still need testing on your server.

## Upstream context

Relevant reports include [extra album credits #15154](https://github.com/jellyfin/jellyfin/issues/15154),
[duplicate artist folders #14666](https://github.com/jellyfin/jellyfin/issues/14666),
and [same-name identities #9623](https://github.com/jellyfin/jellyfin/issues/9623).
This plugin does not claim to fix those upstream bugs or safely merge identities.

MIT for Artist Pruner; see LICENSE. TagLibSharp is LGPL-2.1-only; see
THIRD-PARTY-NOTICES.md and TagLibSharp-LICENSE.txt.
