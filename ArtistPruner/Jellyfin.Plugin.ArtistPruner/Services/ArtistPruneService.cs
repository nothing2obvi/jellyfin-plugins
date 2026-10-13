using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ArtistPruner.Configuration;
using Jellyfin.Plugin.ArtistPruner.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArtistPruner.Services;

/// <summary>
/// Audit with optional reversible album-credit corrections delegated to CleanupService.
/// Names absent from probe results are evidence for review, never proof of an orphan.
/// </summary>
public sealed class ArtistPruneService : IArtistPruneService
{
    private readonly ILibraryManager _library;
    private readonly IMediaEncoder _encoder;
    private readonly ILogger<ArtistPruneService> _logger;
    private readonly Func<PluginConfiguration> _configuration;
    private readonly SemaphoreSlim _gate;
    private readonly CleanupService? _cleanup;
    private readonly PreviewStore? _preview;

    public ArtistPruneService(ILibraryManager library, IMediaEncoder encoder, ILogger<ArtistPruneService> logger, CleanupService cleanup, PreviewStore preview)
        : this(library, encoder, logger, () => Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Artist Pruner is not initialized."), cleanup, preview) { }

    public ArtistPruneService(ILibraryManager library, IMediaEncoder encoder,
        ILogger<ArtistPruneService> logger, Func<PluginConfiguration> configuration, CleanupService? cleanup = null, PreviewStore? preview = null)
    {
        _library = library;
        _encoder = encoder;
        _logger = logger;
        _configuration = configuration;
        _cleanup = cleanup;
        _preview = preview;
        _gate = cleanup?.Gate ?? new SemaphoreSlim(1, 1);
    }

    public async Task<PruneResult> PruneAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("An Artist Pruner audit is already running.");
        try
        {
            var config = _configuration();
            var result = new PruneResult { WasDryRun = config.DryRun };
            var plans = new List<AlbumChange>();
            _preview?.Begin(result.WasDryRun);
            if (!config.Enabled)
            {
                result.Summary = "Artist Pruner is disabled.";
                _logger.LogInformation("{Summary}", result.Summary);
                _preview?.Finish("Disabled", result.Summary);
                return result;
            }
            if (!result.WasDryRun && _cleanup is null)
                throw new InvalidOperationException("Cleanup service is unavailable.");
            CheckScan(cancellationToken);
            progress?.Report(0);
            var libraryIds = _library.GetVirtualFolders()
                .Where(folder => folder.CollectionType == CollectionTypeOptions.music)
                .Select(folder => Guid.Parse(folder.ItemId)).ToArray();
            if (libraryIds.Length == 0)
            {
                result.Summary = "No music libraries configured; no changes made.";
                _logger.LogInformation("{Summary}", result.Summary);
                progress?.Report(100);
                _preview?.Finish("Completed", result.Summary);
                return result;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            var credits = new Dictionary<Guid, (string[] Artists, string[] AlbumArtists)>();
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Audio], AncestorIds = libraryIds,
                Recursive = true, GroupByPresentationUniqueKey = false,
                EnableTotalRecordCount = false
            };
            // Snapshot IDs once. Paging filtered results can skip items or loop forever.
            var trackIds = _library.GetItemIds(query);
            for (var index = 0; index < trackIds.Count; index++)
            {
                CheckScan(cancellationToken);
                if (_library.GetItemById(trackIds[index]) is not Audio track || !track.IsFileProtocol
                    || string.IsNullOrWhiteSpace(track.Path) || !File.Exists(track.Path))
                {
                    result.TracksSkipped++;
                    _logger.LogWarning("Skipping unavailable/non-file music item {ItemId}", trackIds[index]);
                    continue;
                }
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    var info = await _encoder.GetMediaInfo(new MediaInfoRequest
                    {
                        MediaSource = new MediaSourceInfo { Path = track.Path, Protocol = MediaProtocol.File },
                        MediaType = DlnaProfileType.Audio, ExtractChapters = false
                    }, timeout.Token).ConfigureAwait(false);
                    CheckScan(cancellationToken);
                    result.TracksInspected++;
                    if (!CreditComparison.HasUsableCredits(info.Artists))
                    {
                        result.TracksSkipped++;
                        _logger.LogWarning("Music item {ItemId} has missing or ambiguous artist credits", track.Id);
                    }
                    if (CreditComparison.HasUsableCredits(info.Artists) && CreditComparison.HasUsableCredits(info.AlbumArtists))
                        credits[track.Id] = (info.Artists, info.AlbumArtists);
                    foreach (var name in info.Artists.Concat(info.AlbumArtists).Where(n => !string.IsNullOrWhiteSpace(n)))
                        names.Add(name);
                    if (!CreditComparison.SameCredits(track.Artists, info.Artists)
                        || !CreditComparison.SameCredits(track.AlbumArtists, info.AlbumArtists))
                    {
                        result.CreditMismatches++;
                        var plan = _cleanup?.PlanTrack(track, (info.Artists, info.AlbumArtists));
                        if (plan is not null) plans.Add(plan);
                        Record(track, "Track", track.Artists.ToArray(), track.AlbumArtists.ToArray(), info.Artists, info.AlbumArtists, plan);
                        _logger.LogInformation(
                            "Credit discrepancy {ItemId}: stored artists {StoredArtists}, probed {ProbedArtists}; stored album artists {StoredAlbumArtists}, probed {ProbedAlbumArtists}. Review only.",
                            track.Id, track.Artists, info.Artists, track.AlbumArtists, info.AlbumArtists);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_library.IsScanRunning)
                {
                    result.TracksSkipped++;
                    _logger.LogWarning("Probe timed out for music item {ItemId}", track.Id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not ScanStartedException)
                {
                    result.TracksSkipped++;
                    _logger.LogWarning(ex, "Could not probe music item {ItemId}; no conclusion drawn", track.Id);
                }
                progress?.Report(60.0 * (index + 1) / Math.Max(1, trackIds.Count));
            }

            var albumIds = _library.GetItemIds(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.MusicAlbum], AncestorIds = libraryIds,
                Recursive = true, GroupByPresentationUniqueKey = false, EnableTotalRecordCount = false
            });
            for (var index = 0; index < albumIds.Count; index++)
            {
                CheckScan(cancellationToken);
                if (_library.GetItemById(albumIds[index]) is not MusicAlbum album) continue;
                var children = _library.GetItemIds(new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Audio], AncestorIds = [album.Id], Recursive = true,
                    GroupByPresentationUniqueKey = false, EnableTotalRecordCount = false
                });
                if (children.Count == 0 || children.Any(id => !credits.ContainsKey(id)))
                {
                    result.AlbumsSkipped++;
                    _logger.LogInformation("Album {AlbumId} has incomplete track evidence; preserved without a conclusion", album.Id);
                }
                else
                {
                    result.AlbumsInspected++;
                    var artists = children.SelectMany(id => credits[id].Artists).Distinct(StringComparer.Ordinal).ToArray();
                    var albumArtists = children.SelectMany(id => credits[id].AlbumArtists).Distinct(StringComparer.Ordinal).ToArray();
                    if (!CreditComparison.SameCredits(album.Artists, artists)
                        || !CreditComparison.SameCredits(album.AlbumArtists, albumArtists))
                    {
                        result.AlbumCreditMismatches++;
                        var plan = _cleanup?.Plan(album, children, credits);
                        Record(album, "Album", album.Artists.ToArray(), album.AlbumArtists.ToArray(), artists, albumArtists, plan);
                        if (plan is not null)
                        {
                            plans.Add(plan);
                            _logger.LogInformation("Proposed album {Id}: {Before} -> {After}; metadata locks will protect the album and removed artists for restoration.", album.Id, plan.Before, plan.After);
                        }
                        _logger.LogInformation(
                            "Album discrepancy {AlbumId}: stored artists {StoredArtists}, probed track union {Artists}; stored album artists {StoredAlbumArtists}, probed track union {AlbumArtists}. Review only.",
                            album.Id, album.Artists, artists, album.AlbumArtists, albumArtists);
                    }
                }
                progress?.Report(60 + 20.0 * (index + 1) / Math.Max(1, albumIds.Count));
            }

            // Artists are global. Restrict inspection to entries with music-library relationships
            // or a music-library parent. Global unassociated artists cannot safely be scoped.
            var artistIds = _library.GetItemIds(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.MusicArtist], GroupByPresentationUniqueKey = false,
                EnableTotalRecordCount = false
            });
            for (var index = 0; index < artistIds.Count; index++)
            {
                CheckScan(cancellationToken);
                if (_library.GetItemById(artistIds[index]) is not MusicArtist artist) continue;
                bool HasItems(bool albumArtist) => _library.GetItemIds(new InternalItemsQuery
                {
                    AncestorIds = libraryIds, Recursive = true,
                    IncludeItemTypes = [BaseItemKind.Audio, BaseItemKind.MusicAlbum],
                    ArtistIds = albumArtist ? [] : [artist.Id],
                    AlbumArtistIds = albumArtist ? [artist.Id] : [],
                    Limit = 1, EnableTotalRecordCount = false, GroupByPresentationUniqueKey = false
                }).Count > 0;
                var hasArtistLinks = HasItems(false);
                var hasAlbumLinks = HasItems(true);
                var scopedFolder = _library.GetItemIds(new InternalItemsQuery
                {
                    ItemIds = [artist.Id], AncestorIds = libraryIds, Recursive = true,
                    IncludeItemTypes = [BaseItemKind.MusicArtist], Limit = 1,
                    EnableTotalRecordCount = false, GroupByPresentationUniqueKey = false
                }).Count > 0;
                if (!hasArtistLinks && !hasAlbumLinks && !scopedFolder) continue;
                result.ArtistsInspected++;
                if (!names.Contains(artist.Name))
                {
                    result.UnsupportedNames++;
                    result.EntriesSkipped++;
                    _logger.LogInformation(
                        "Artist {ArtistId} ({Name}) absent from successfully probed music credits. Artist links: {ArtistLinks}; album links: {AlbumLinks}; folder: {Folder}; provider IDs: {ProviderIds}. Preserved: name differences do not establish identity or safe deletion.",
                        artist.Id, artist.Name, hasArtistLinks, hasAlbumLinks, !artist.IsAccessedByName, artist.ProviderIds);
                }
                progress?.Report(80 + 20.0 * (index + 1) / Math.Max(1, artistIds.Count));
            }
            CheckScan(cancellationToken);
            result.ChangesProposed = plans.Count;
            if (!result.WasDryRun) result.AssociationsCorrected = _cleanup!.Apply(plans, cancellationToken);
            result.Summary = $"{(result.WasDryRun ? "Dry run" : "Cleanup")}: {result.TracksInspected} tracks probed, {result.TracksSkipped} unavailable/ambiguous, {result.CreditMismatches} track credit discrepancies; {result.AlbumsInspected} albums inspected, {result.AlbumsSkipped} albums inconclusive, {result.AlbumCreditMismatches} album credit discrepancies; {result.ArtistsInspected} artists inspected, {result.UnsupportedNames} names need review. Eligible items: {plans.Count}; items corrected: {result.AssociationsCorrected}; entries removed: 0; entries skipped: {result.EntriesSkipped}.";
            _logger.LogInformation("{Summary}", result.Summary);
            _preview?.Finish("Completed", result.Summary);
            progress?.Report(100);
            return result;
        }
        catch (Exception ex)
        {
            _preview?.Finish(ex is OperationCanceledException ? "Cancelled" : "Failed", ex.Message);
            throw;
        }
        finally { _gate.Release(); }
    }

    private void Record(BaseItem item, string kind, string[] artists, string[] albumArtists,
        string[] fileArtists, string[] fileAlbumArtists, AlbumChange? plan)
    {
        void Field(string field, string[] before, string[] evidence, string[]? after)
        {
            if (CreditComparison.SameCredits(before, evidence)) return;
            var removed = after is null ? Array.Empty<string>() : before.Except(after, StringComparer.Ordinal).ToArray();
            var status = removed.Length > 0 ? "Proposed" : "Review only — not eligible (replacement, missing/ambiguous tags, lock, or identity check)";
            _preview?.Add(new(item.Id, item.Name, kind, field, before.ToArray(), evidence.ToArray(), removed, status));
            if (removed.Length > 0)
                _logger.LogInformation("Proposed prune {Id} ({Name}), {Field}: remove {Removed}; retain {After}", item.Id, item.Name, field, removed, after);
        }
        Field("Artists", artists, fileArtists, plan?.AfterArtists);
        Field("Album artists", albumArtists, fileAlbumArtists, plan?.After);
    }

    private void CheckScan(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_library.IsScanRunning)
            throw new ScanStartedException();
    }

    private sealed class ScanStartedException : InvalidOperationException
    {
        public ScanStartedException() : base("A library scan is running. Audit stopped; run again after the scan finishes.") { }
    }
}
