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
    {
        if (album.IsLocked || album.LockedFields.Length > 0 || tracks.Count == 0) return null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in tracks)
        {
            if (_library.GetItemById(id) is not Audio track || track.IsLocked || !track.IsFileProtocol
                || !probed.TryGetValue(id, out var probe)) return null;
            (string[] Artists, string[] AlbumArtists) embedded;
            try { embedded = EmbeddedCredits.Read(track.Path); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidDataException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException)
            { _logger.LogInformation("Skipping album {Id}: {Reason}", album.Id, ex.Message); return null; }
            if (!CreditComparison.SameCredits(embedded.Artists, track.Artists)
                || !CreditComparison.SameCredits(embedded.Artists, probe.Artists)
                || !CreditComparison.SameCredits(embedded.AlbumArtists, track.AlbumArtists)
                || !CreditComparison.SameCredits(embedded.AlbumArtists, probe.AlbumArtists)) return null;
            names.UnionWith(embedded.AlbumArtists);
        }
        var before = album.AlbumArtists.ToArray();
        // Never add/rename credits or change the primary artist (also used in user-data keys).
        if (before.Length == 0 || !names.Contains(before[0]) || !names.IsSubsetOf(before)
            || names.Count >= before.Length || before.Distinct(StringComparer.Ordinal).Count() != before.Length) return null;
        var after = before.Where(names.Contains).ToArray();
        var removed = before.Except(after, StringComparer.Ordinal).ToArray();
        var protections = new List<ArtistProtection>();
        foreach (var name in removed)
        {
            var artists = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.MusicArtist], Name = name,
#if JELLYFIN12
                UseRawName = true,
#endif
                GroupByPresentationUniqueKey = false, EnableTotalRecordCount = false
            }).OfType<MusicArtist>().Where(a => a.Name == name).ToArray();
            // Ambiguous identity or missing entity: do not alter its relationship.
            if (artists.Length != 1) return null;
            var artist = artists[0];
            protections.Add(new ArtistProtection { Id = artist.Id, Name = artist.Name, Path = artist.Path ?? "",
                ParentId = artist.ParentId, WasLocked = artist.IsLocked, ProviderIds = new(artist.ProviderIds) });
        }
        return new AlbumChange { Id = album.Id, Path = album.Path, ParentId = album.ParentId,
            Before = before, After = after, Artists = album.Artists.ToArray(), TrackIds = tracks.ToArray(),
            ProviderIds = new(album.ProviderIds), ProtectedArtists = protections };
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
        _logger.LogInformation("Corrected {Count} albums; recovery point {Id}. Affected albums/artists are locked until restored or manually unlocked.", plans.Count, point.Id);
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
                Credits: (item as MusicAlbum)?.AlbumArtists.ToArray())).ToArray();
            Check(token);
            Set(items, point.Changes, restoring: true);
            try { _writer.SaveItems(items, token); }
            catch
            {
                foreach (var snapshot in old)
                {
                    snapshot.Item.IsLocked = snapshot.Locked;
                    if (snapshot.Item is MusicAlbum album) album.AlbumArtists = snapshot.Credits!;
                }
                throw;
            }
            point.State = "Restored";
            _store.Save(point);
            _store.Retain(Plugin.Instance?.Configuration.BackupRetentionCount ?? 5);
            _logger.LogInformation("Restored recovery point {Id}", id);
            return $"Restored {point.Changes.Count} albums and their previous metadata locks.";
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
            if (_library.GetItemById(plan.Id) is not MusicAlbum album || album.Path != plan.Path
                || album.ParentId != plan.ParentId || !SameIds(album.ProviderIds, plan.ProviderIds)
                || !album.Artists.SequenceEqual(plan.Artists)
                || _library.GetItemIds(new InternalItemsQuery { ItemIds = [plan.Id], AncestorIds = music,
                    IncludeItemTypes = [BaseItemKind.MusicAlbum], Recursive = true, Limit = 1 }).Count != 1)
                throw new InvalidOperationException($"Album {plan.Id} changed or left the music library; no changes applied.");
            var before = album.AlbumArtists.SequenceEqual(plan.Before) && !album.IsLocked;
            var after = album.AlbumArtists.SequenceEqual(plan.After) && album.IsLocked;
            if (restoring ? !before && !after : !before || album.LockedFields.Length > 0)
                throw new InvalidOperationException($"Album {plan.Id} has conflicting edits; no changes applied.");
            if (!restoring)
            {
                var children = _library.GetItemIds(new InternalItemsQuery { AncestorIds = [album.Id], Recursive = true,
                    IncludeItemTypes = [BaseItemKind.Audio], GroupByPresentationUniqueKey = false });
                if (!new HashSet<Guid>(children).SetEquals(plan.TrackIds))
                    throw new InvalidOperationException("Album membership changed since preview.");
                // Fresh raw tags + current track credits must still justify this exact removal.
                var evidence = plan.TrackIds.ToDictionary(trackId => trackId, trackId =>
                {
                    var track = _library.GetItemById(trackId) as Audio ?? throw new InvalidOperationException("Track disappeared.");
                    return (track.Artists.ToArray(), track.AlbumArtists.ToArray());
                });
                var fresh = Plan(album, children, evidence);
                if (fresh is null || !fresh.After.SequenceEqual(plan.After)
                    || !fresh.ProtectedArtists.Select(a => a.Id).SequenceEqual(plan.ProtectedArtists.Select(a => a.Id)))
                    throw new InvalidOperationException("Embedded credits or artist identities changed; run preview again.");
            }
            items[album.Id] = album;
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
            var album = (MusicAlbum)byId[plan.Id];
            album.AlbumArtists = restoring ? plan.Before : plan.After;
            album.IsLocked = !restoring;
            foreach (var saved in plan.ProtectedArtists) byId[saved.Id].IsLocked = restoring ? saved.WasLocked : true;
        }
    }
    private void Check(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_library.IsScanRunning) throw new InvalidOperationException("Library scan running; retry after it finishes.");
    }
}
