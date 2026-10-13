using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ArtistPruner.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using Microsoft.Extensions.Logging;
#if JELLYFIN12
using ItemWriter = MediaBrowser.Controller.Persistence.IItemPersistenceService;
#else
using ItemWriter = MediaBrowser.Controller.Persistence.IItemRepository;
#endif

namespace Jellyfin.Plugin.ArtistPruner.Services;

/// <summary>Reversible removal of surplus album-artist credits; no entity/file deletion.</summary>
public sealed class CleanupService
{
    private readonly ILibraryManager _library;
    private readonly ItemWriter _writer;
    private readonly RecoveryStore _store;
    private readonly ILogger<CleanupService> _logger;
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public CleanupService(ILibraryManager library, ItemWriter writer, RecoveryStore store, ILogger<CleanupService> logger)
    { _library = library; _writer = writer; _store = store; _logger = logger; }

    public AlbumChange? Plan(MusicAlbum album, IReadOnlyList<Guid> tracks,
        IReadOnlyDictionary<Guid, (string[] Artists, string[] AlbumArtists)> probed)
        => PlanItem(album, tracks, probed);

    public AlbumChange? PlanTrack(Audio track, (string[] Artists, string[] AlbumArtists) probe)
        => PlanItem(track, [track.Id], new Dictionary<Guid, (string[], string[])> { [track.Id] = probe });

    private AlbumChange? PlanItem(BaseItem item, IReadOnlyList<Guid> tracks,
        IReadOnlyDictionary<Guid, (string[] Artists, string[] AlbumArtists)> probed)
    {
        if (item.IsLocked || item.LockedFields.Length > 0 || tracks.Count == 0) return null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var performers = new HashSet<string>(StringComparer.Ordinal);
        var evidence = new Dictionary<Guid, string[]>();
        foreach (var id in tracks)
        {
            if (_library.GetItemById(id) is not Audio track || !track.IsFileProtocol
                || !probed.TryGetValue(id, out var probe)) return null;
            (string[] Artists, string[] AlbumArtists) embedded;
            try { embedded = EmbeddedCredits.Read(track.Path); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidDataException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException)
            { _logger.LogInformation("Skipping item {Id}: {Reason}", item.Id, ex.Message); return null; }
            // The database is what we correct, not an additional source of truth.
            if (!CreditComparison.SameCredits(embedded.Artists, probe.Artists)
                || !CreditComparison.SameCredits(embedded.AlbumArtists, probe.AlbumArtists)) return null;
            evidence[id] = [System.Text.Json.JsonSerializer.Serialize(embedded.Artists), System.Text.Json.JsonSerializer.Serialize(embedded.AlbumArtists)];
            names.UnionWith(embedded.AlbumArtists);
            performers.UnionWith(embedded.Artists);
        }
        var (oldArtists, before) = Credits(item);
        var after = Pruned(before, names);
        var afterArtists = Pruned(oldArtists, performers);
        if (after is null || afterArtists is null
            || (before.SequenceEqual(after) && oldArtists.SequenceEqual(afterArtists))) return null;
        var removed = before.Except(after, StringComparer.Ordinal)
            .Concat(oldArtists.Except(afterArtists, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal);
        var protections = new List<ArtistProtection>();
        foreach (var name in removed)
        {
            // Avoid version-specific UseRawName API. Exact ordinal post-filtering is mandatory.
            var artists = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.MusicArtist], Name = name,
                GroupByPresentationUniqueKey = false, EnableTotalRecordCount = false
            }).OfType<MusicArtist>().Where(a => a.Name == name).ToArray();
            if (artists.Length != 1) return null;
            var artist = artists[0];
            protections.Add(new ArtistProtection { Id = artist.Id, Name = artist.Name, Path = artist.Path ?? "",
                ParentId = artist.ParentId, WasLocked = artist.IsLocked, ProviderIds = new(artist.ProviderIds) });
        }
        return new AlbumChange { Id = item.Id, Kind = item is Audio ? "Audio" : "MusicAlbum", Path = item.Path,
            ParentId = item.ParentId, Before = before, After = after, Artists = oldArtists,
            AfterArtists = afterArtists, Evidence = evidence, TrackIds = tracks.ToArray(),
            ProviderIds = new(item.ProviderIds), ProtectedArtists = protections };
    }

    private static string[]? Pruned(string[] before, HashSet<string> embedded)
    {
        // Removal only: never empty a credit field, change its primary, or invent a replacement.
        if (before.Length == 0 || !embedded.Contains(before[0]) || !embedded.IsSubsetOf(before)
            || before.Distinct(StringComparer.Ordinal).Count() != before.Length) return null;
        return before.Where(embedded.Contains).ToArray();
    }

    private static (string[] Artists, string[] AlbumArtists) Credits(BaseItem item) => item switch
    {
        Audio a => (a.Artists.ToArray(), a.AlbumArtists.ToArray()),
        MusicAlbum a => (a.Artists.ToArray(), a.AlbumArtists.ToArray()),
        _ => throw new InvalidOperationException("Unsupported credit item.")
    };
    private static void WriteCredits(BaseItem item, string[] artists, string[] albumArtists)
    {
        if (item is Audio audio) { audio.Artists = artists; audio.AlbumArtists = albumArtists; }
        else if (item is MusicAlbum album) { album.Artists = artists; album.AlbumArtists = albumArtists; }
    }

    // Caller holds Gate for the entire audit + apply. Persist the whole batch atomically via
    // Jellyfin's public repository service, avoiding metadata savers and music/NFO writes.
    public int Apply(List<AlbumChange> plans, CancellationToken token)
    {
        if (plans.Count == 0) return 0;
        Check(token);
        if (_store.List().Any(p => p.State == "Prepared"))
            throw new InvalidOperationException("Restore the pending recovery point before another cleanup.");
        var items = Resolve(plans, restoring: false);
        var point = new RecoveryPoint { Changes = plans };
        _store.Save(point, create: true); // Includes durable flush, checksum and read-back.
        Check(token);
        items = Resolve(plans, restoring: false);
        Check(token);
        Set(items, plans, restoring: false);
        try
        {
            _writer.SaveItems(items, token);
        }
        catch
        {
            Set(items, plans, restoring: true); // Restore cached objects; journal stays Prepared.
            throw;
        }
        point.State = "Applied";
        _store.Save(point);
        _logger.LogInformation("Corrected {Count} items; recovery point {Id}. Affected albums/artists are locked until restored or manually unlocked.", plans.Count, point.Id);
        return plans.Count;
    }

    public async Task<string> Restore(Guid id, CancellationToken token)
    {
        if (!await Gate.WaitAsync(0, token)) throw new InvalidOperationException("Artist Pruner is already running.");
        try
        {
            Check(token);
            var point = _store.Load(id);
            if (point.State == "Restored") return "Already restored; no changes made.";
            if (_store.List().FirstOrDefault(p => p.State != "Restored")?.Id != id)
                throw new InvalidOperationException("Restore newer recovery points first to preserve shared artist locks.");
            var items = Resolve(point.Changes, restoring: true);
            // Capture only mutable fields for failure recovery; unrelated edits stay on the current objects.
            var old = items.Select(item => (Item: item, Locked: item.IsLocked,
                Credits: item is Audio or MusicAlbum ? Credits(item) : ((string[] Artists, string[] AlbumArtists)?)null)).ToArray();
            Check(token);
            Set(items, point.Changes, restoring: true);
            try { _writer.SaveItems(items, token); }
            catch
            {
                foreach (var snapshot in old)
                {
                    snapshot.Item.IsLocked = snapshot.Locked;
                    if (snapshot.Credits is {} credits) WriteCredits(snapshot.Item, credits.Artists, credits.AlbumArtists);
                }
                throw;
            }
            point.State = "Restored";
            _store.Save(point);
            _store.Retain(Plugin.Instance?.Configuration.BackupRetentionCount ?? 5);
            _logger.LogInformation("Restored recovery point {Id}", id);
            return $"Restored {point.Changes.Count} items and their previous metadata locks.";
        }
        finally { Gate.Release(); }
    }

    private List<BaseItem> Resolve(List<AlbumChange> plans, bool restoring)
    {
        var items = new Dictionary<Guid, BaseItem>();
        var music = _library.GetVirtualFolders().Where(f => f.CollectionType == MediaBrowser.Model.Entities.CollectionTypeOptions.music)
            .Select(f => Guid.Parse(f.ItemId)).ToArray();
        if (music.Length == 0) throw new InvalidOperationException("No music libraries configured.");
        foreach (var plan in plans)
        {
            var item = _library.GetItemById(plan.Id);
            var kind = plan.Kind == "Audio" ? BaseItemKind.Audio : BaseItemKind.MusicAlbum;
            if (item is null || (plan.Kind == "Audio" ? item is not Audio : item is not MusicAlbum)
                || item.Path != plan.Path || item.ParentId != plan.ParentId || !SameIds(item.ProviderIds, plan.ProviderIds)
                || _library.GetItemIds(new InternalItemsQuery { ItemIds = [plan.Id], AncestorIds = music,
                    IncludeItemTypes = [kind], Recursive = true, Limit = 1 }).Count != 1)
                throw new InvalidOperationException($"Item {plan.Id} changed or left the music library; no changes applied.");
            var current = Credits(item);
            var before = current.AlbumArtists.SequenceEqual(plan.Before) && current.Artists.SequenceEqual(plan.Artists) && !item.IsLocked;
            var after = current.AlbumArtists.SequenceEqual(plan.After) && current.Artists.SequenceEqual(plan.AfterArtists ?? plan.Artists) && item.IsLocked;
            if (restoring ? !before && !after : !before || item.LockedFields.Length > 0)
                throw new InvalidOperationException($"Item {plan.Id} has conflicting edits; no changes applied.");
            if (!restoring)
            {
                IReadOnlyList<Guid> children = item is Audio ? new[] { item.Id } : _library.GetItemIds(new InternalItemsQuery
                { AncestorIds = [item.Id], Recursive = true, IncludeItemTypes = [BaseItemKind.Audio], GroupByPresentationUniqueKey = false });
                if (!new HashSet<Guid>(children).SetEquals(plan.TrackIds))
                    throw new InvalidOperationException("Album membership changed since preview.");
                var evidence = plan.TrackIds.ToDictionary(trackId => trackId, trackId =>
                {
                    var track = _library.GetItemById(trackId) as Audio ?? throw new InvalidOperationException("Track disappeared.");
                    var raw = EmbeddedCredits.Read(track.Path);
                    if (plan.Evidence.TryGetValue(trackId, out var saved)
                        && !saved.SequenceEqual(new[] { System.Text.Json.JsonSerializer.Serialize(raw.Artists), System.Text.Json.JsonSerializer.Serialize(raw.AlbumArtists) }))
                        throw new InvalidOperationException("Embedded credits changed; run preview again.");
                    return raw;
                });
                var fresh = PlanItem(item, children, evidence);
                if (fresh is null || !fresh.After.SequenceEqual(plan.After)
                    || !(fresh.AfterArtists ?? fresh.Artists).SequenceEqual(plan.AfterArtists ?? plan.Artists)
                    || !fresh.ProtectedArtists.Select(a => a.Id).SequenceEqual(plan.ProtectedArtists.Select(a => a.Id)))
                    throw new InvalidOperationException("Embedded credits or artist identities changed; run preview again.");
            }
            items[item.Id] = item;
            foreach (var saved in plan.ProtectedArtists)
            {
                if (_library.GetItemById(saved.Id) is not MusicArtist artist || artist.Name != saved.Name
                    || (artist.Path ?? "") != saved.Path || artist.ParentId != saved.ParentId
                    || !SameIds(artist.ProviderIds, saved.ProviderIds)
                    || (!restoring && artist.IsLocked != saved.WasLocked)
                    || (restoring && !artist.IsLocked && saved.WasLocked))
                    throw new InvalidOperationException($"Artist {saved.Id} has conflicting edits or is missing; no changes applied.");
                items[artist.Id] = artist;
            }
        }
        return items.Values.ToList();
    }

    private static bool SameIds(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && pair.Value == value);
    private static void Set(List<BaseItem> items, List<AlbumChange> plans, bool restoring)
    {
        var byId = items.ToDictionary(i => i.Id);
        foreach (var plan in plans)
        {
            var item = byId[plan.Id];
            WriteCredits(item, restoring ? plan.Artists : plan.AfterArtists ?? plan.Artists, restoring ? plan.Before : plan.After);
            item.IsLocked = !restoring;
            foreach (var saved in plan.ProtectedArtists) byId[saved.Id].IsLocked = restoring ? saved.WasLocked : true;
        }
    }
    private void Check(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_library.IsScanRunning) throw new InvalidOperationException("Library scan running; retry after it finishes.");
    }
}
