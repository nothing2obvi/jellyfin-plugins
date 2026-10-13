using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ArtistPruner.Models;
using Jellyfin.Plugin.ArtistPruner.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
#if JELLYFIN12
using ItemWriter = MediaBrowser.Controller.Persistence.IItemPersistenceService;
#else
using ItemWriter = MediaBrowser.Controller.Persistence.IItemRepository;
#endif

static class FullModeTests
{
    public static async Task Run(Action<bool, string> check)
    {
        async Task Reject(Func<Task> call, string label)
        {
            try { await call(); } catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or System.Text.Json.JsonException)
            { check(true, label); return; }
            throw new Exception("Expected rejection: " + label);
        }
        using var f = new FullFixture();
        var raw = EmbeddedCredits.Read(f.Track.Path);
        check(raw.Artists.SequenceEqual(new[] { "Utada Hikaru" }) && raw.AlbumArtists.SequenceEqual(raw.Artists), "real FLAC embedded tags read without modification");
        var plan = f.Plan();
        check(plan is not null && plan.After.SequenceEqual(raw.Artists), "eligible extra album credit proposed");
        check(f.Writes == 0 && f.Store.List().Count == 0 && !f.Album.IsLocked, "planning/dry-run creates no backups or changes");
        var beforeBytes = File.ReadAllBytes(f.Track.Path);
        var corrected = f.Cleanup.Apply([plan!], default);
        var point = f.Store.List().Single();
        check(corrected == 1 && point.State == "Applied" && f.Writes == 1, "full mode creates validated recovery point and persists correction");
        check(f.Album.AlbumArtists.SequenceEqual(raw.Artists) && f.Album.IsLocked && f.Artist.IsLocked, "corrected album and retained artist protected from rescans");
        check(f.Plan() is null, "repeated cleanup is idempotent");
        check(File.ReadAllBytes(f.Track.Path).SequenceEqual(beforeBytes), "full mode does not write music file");
        f.Album.Overview = "New unrelated edit";
        await f.Cleanup.Restore(point.Id, default);
        check(f.Album.AlbumArtists.SequenceEqual(plan!.Before) && !f.Album.IsLocked && !f.Artist.IsLocked, "restore reinstates exact credits and original locks");
        check(f.Album.Overview == "New unrelated edit" && f.Artist.ProviderIds["MusicBrainzArtist"] == "retained-provider-id", "restore preserves unrelated metadata and provider IDs");
        var writes = f.Writes;
        await f.Cleanup.Restore(point.Id, default);
        check(f.Writes == writes, "repeat restoration performs no writes");
        f.Cleanup.Apply([f.Plan()!], default);
        point = f.Store.List().First(p => p.State != "Restored");
        f.Album.AlbumArtists = ["User changed this"];
        await Reject(() => f.Cleanup.Restore(point.Id, default), "restore refuses to overwrite changed credits");
        check(f.Album.AlbumArtists[0] == "User changed this", "conflicting edit preserved");
        f.Album.AlbumArtists = plan.After;
        f.Scanning = true;
        await Reject(() => f.Cleanup.Restore(point.Id, default), "restoration blocked during scan");
        f.Scanning = false;
        await f.Cleanup.Gate.WaitAsync();
        await Reject(() => f.Cleanup.Restore(point.Id, default), "restore cannot overlap cleanup");
        f.Cleanup.Gate.Release();
        point.State = "Prepared"; f.Store.Save(point);
        await f.Cleanup.Restore(point.Id, default);
        check(!f.Artist.IsLocked && f.Album.AlbumArtists.SequenceEqual(plan.Before), "recovery after commit but before journal completion");
        f.FailWrite = true;
        await Reject(() => Task.FromResult(f.Cleanup.Apply([f.Plan()!], default)), "persistence failure leaves recovery journal");
        point = f.Store.List().First(p => p.State == "Prepared");
        check(!f.Album.IsLocked && f.Album.AlbumArtists.SequenceEqual(plan.Before), "cache state reset after persistence failure");
        f.FailWrite = false;
        await Reject(() => Task.FromResult(f.Cleanup.Apply([f.Plan()!], default)), "pending recovery blocks further cleanup");
        await f.Cleanup.Restore(point.Id, default);
        check(f.Store.Load(point.Id).State == "Restored", "prepared uncommitted backup can be restored safely");
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            try { f.Cleanup.Apply([f.Plan()!], cts.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { check(true, "full mode honors cancellation before any mutation"); }
        }
        plan = f.Plan();
        f.Album.AlbumArtists = ["Changed album"];
        await Reject(() => Task.FromResult(f.Cleanup.Apply([plan!], default)), "stale plan rejected when album metadata changes");
        f.Album.AlbumArtists = plan!.Before;
        f.Album.AlbumArtists = ["宇多田ヒカル", "Utada Hikaru"];
        check(f.Plan() is null, "primary album artist is never removed");
        f.Album.AlbumArtists = ["Utada Hikaru", "宇多田ヒカル"];
        f.DuplicateIdentity = true;
        check(f.Plan() is null, "same-name ambiguous artist identities skipped");
        f.DuplicateIdentity = false;
        f.Album.IsLocked = true;
        check(f.Plan() is null, "existing metadata lock respected");
        f.Album.IsLocked = false;
        var validPlan = f.Plan()!;
        var pending = new RecoveryPoint { Changes = [validPlan] };
        f.Store.Save(pending, true);
        File.AppendAllText(Path.Combine(f.Directory, pending.Id.ToString("N") + ".json"), "corrupt");
        writes = f.Writes;
        await Reject(() => Task.FromResult(f.Cleanup.Apply([validPlan], default)), "corrupt backup blocks cleanup");
        check(f.Writes == writes, "no persistence after failed backup validation");
        await Reject(() => f.Cleanup.Restore(pending.Id, default), "corrupt backup cannot be restored");
        // Verify checksum mismatch separately from malformed JSON.
        f.Store.Save(pending);
        var backupFile = Path.Combine(f.Directory, pending.Id.ToString("N") + ".json");
        File.WriteAllText(backupFile, File.ReadAllText(backupFile).Replace("Prepared", "Restored"));
        await Reject(() => f.Cleanup.Restore(pending.Id, default), "checksum detects valid JSON payload tampering");
        f.Store.Save(pending);
        for (var i = 0; i < 7; i++) f.Store.Save(new RecoveryPoint { State = "Restored", Changes = [validPlan] }, true);
        f.Store.Retain(5);
        check(f.Store.List().Count(p => p.State == "Restored") == 5 && f.Store.Load(pending.Id).State == "Prepared", "retention keeps five restored points and all unresolved recovery data");
        var newer = new RecoveryPoint { Changes = [validPlan] };
        f.Store.Save(newer, true);
        await Reject(() => f.Cleanup.Restore(pending.Id, default), "newer recovery points must be restored first");
        File.WriteAllText(Path.Combine(f.Directory, "incomplete.tmp"), "partial data");
        check(f.Store.List().All(p => p.Id != Guid.Empty), "incomplete temporary writes are never recovery points");
        using (var trackFixture = new FullFixture())
        {
            trackFixture.Track.Artists = ["Utada Hikaru", "宇多田ヒカル"];
            var trackPlan = trackFixture.Cleanup.PlanTrack(trackFixture.Track, raw);
            check(trackPlan?.Kind == "Audio" && trackPlan.AfterArtists!.SequenceEqual(raw.Artists), "extra track artist is proposed from physical tags despite stored disagreement");
            trackFixture.Cleanup.Apply([trackPlan!], default);
            check(trackFixture.Track.Artists.SequenceEqual(raw.Artists) && trackFixture.Track.IsLocked, "full mode prunes track performer and locks metadata");
            await trackFixture.Cleanup.Restore(trackFixture.Store.List().Single().Id, default);
            check(trackFixture.Track.Artists.Count == 2 && !trackFixture.Track.IsLocked, "track credits and locks restored");
            trackFixture.Track.Artists = ["Cubic U"];
            check(trackFixture.Cleanup.PlanTrack(trackFixture.Track, raw) is null, "replacement or empty artist result is never proposed");
            trackFixture.Track.Artists = raw.Artists;
            trackFixture.Track.AlbumArtists = ["Utada Hikaru", "宇多田ヒカル"];
            trackPlan = trackFixture.Cleanup.PlanTrack(trackFixture.Track, raw);
            check(trackPlan is not null && trackPlan.After.SequenceEqual(raw.AlbumArtists), "extra track album artist is proposed");
            trackFixture.Cleanup.Apply([trackPlan!], default);
            await trackFixture.Cleanup.Restore(trackFixture.Store.List().First(p => p.State != "Restored").Id, default);
            check(trackFixture.Track.AlbumArtists.Count == 2, "track album artist restoration preserves original credits");
            trackFixture.Album.Artists = ["Utada Hikaru", "宇多田ヒカル"];
            var albumPlan = trackFixture.Cleanup.Plan(trackFixture.Album, [trackFixture.Track.Id], new Dictionary<Guid, (string[], string[])> { [trackFixture.Track.Id] = raw });
            check(albumPlan?.AfterArtists?.Length == 1, "album performer credits use union of physical track tags");
            var previewPath = Path.Combine(trackFixture.Directory, "preview-report.data");
            var preview = new PreviewStore(previewPath);
            preview.Begin(true);
            preview.Add(new(trackFixture.Track.Id, "<script>name</script>", "Track", "Artists", ["A", "B"], ["A"], ["B"], "Proposed"));
            check(preview.Read()?.Status == "Running" && preview.Read()?.Rows.Count == 1, "in-progress report is marked running");
            preview.Finish("Failed", "test failure");
            check(new PreviewStore(previewPath).Read()?.Status == "Failed" && new PreviewStore(previewPath).Read()?.Rows.Count == 1, "failed partial preview persists across restart");
            preview.Begin(true);
            check(new PreviewStore(previewPath).Read()?.Status == "Interrupted", "unfinished persisted report is not presented as complete");
            preview.Finish("Completed", "zero eligible changes");
            check(preview.Read()?.Rows.Count == 0, "new run clears stale preview rows");
        }
        using (var legacy = new FullFixture())
        {
            var oldPlan = legacy.Plan()!;
            oldPlan.AfterArtists = null;
            oldPlan.Evidence.Clear();
            var oldPoint = new RecoveryPoint { Schema = 1, Changes = [oldPlan], State = "Applied" };
            legacy.Album.AlbumArtists = oldPlan.After;
            legacy.Album.IsLocked = true;
            legacy.Artist.IsLocked = true;
            legacy.Store.Save(oldPoint, true);
            await legacy.Cleanup.Restore(oldPoint.Id, default);
            check(legacy.Album.AlbumArtists.Count == 2 && !legacy.Artist.IsLocked, "previous-release schema-one recovery points remain restorable");
        }
        using (var integrated = new FullFixture())
        {
            integrated.Track.Artists = ["Utada Hikaru", "宇多田ヒカル"];
            var preview = new PreviewStore(Path.Combine(integrated.Directory, "report.data"));
            var config = new Jellyfin.Plugin.ArtistPruner.Configuration.PluginConfiguration { DryRun = true };
            var encoder = StrictProxy.Create<MediaBrowser.Controller.MediaEncoding.IMediaEncoder>((method, args) =>
                method.Name == "GetMediaInfo" ? Task.FromResult(new MediaBrowser.Model.MediaInfo.MediaInfo { Artists = raw.Artists, AlbumArtists = raw.AlbumArtists })
                    : throw new Exception("Unexpected encoder call"));
            var service = new ArtistPruneService(integrated.Library, encoder, NullLogger<ArtistPruneService>.Instance, () => config, integrated.Cleanup, preview);
            var result = await service.PruneAsync(null, default);
            check(result.ChangesProposed == 2 && integrated.Writes == 0 && preview.Read()?.Status == "Completed", "scheduled service dry-run reports track and album proposals without mutation");
            check(preview.Read()!.Rows.Count(r => r.Status == "Proposed") == 2, "preview rows match eligible logged proposals");
            config.DryRun = false;
            result = await service.PruneAsync(null, default);
            check(result.AssociationsCorrected == 2 && integrated.Writes == 1, "scheduled service full mode persists track and album in one batch");
            await integrated.Cleanup.Restore(integrated.Store.List().Single().Id, default);
            check(integrated.Track.Artists.Count == 2 && integrated.Album.AlbumArtists.Count == 2 && !integrated.Artist.IsLocked, "combined batch restoration restores shared artist protection exactly");
            using (var file = TagLib.File.Create(integrated.Track.Path))
            {
                ((TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph, true)).SetField("ARTISTS", "Utada Hikaru", "宇多田ヒカル");
                file.Save();
            }
            check(integrated.Cleanup.PlanTrack(integrated.Track, raw) is null, "alternate plural tags cannot justify collaborator removal");
        }
        var badPath = Path.Combine(f.Directory, "not-a-directory");
        File.WriteAllText(badPath, "block");
        await Reject(() => Task.Run(() => new RecoveryStore(badPath).Save(new RecoveryPoint { Changes = [validPlan] }, true)), "backup creation failure is surfaced");
    }
}

sealed class FullFixture : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "artistpruner-full-" + Guid.NewGuid());
    public MusicAlbum Album { get; }
    public Audio Track { get; }
    public MusicArtist Artist { get; }
    public RecoveryStore Store { get; }
    public CleanupService Cleanup { get; }
    public ILibraryManager Library { get; }
    public bool Scanning { get; set; }
    public bool FailWrite { get; set; }
    public bool DuplicateIdentity { get; set; }
    public int Writes { get; private set; }
    private readonly Guid _libraryId = Guid.NewGuid();
    public FullFixture()
    {
        System.IO.Directory.CreateDirectory(Directory);
        var audioPath = Path.Combine(Directory, "track.flac");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "credits.flac"), audioPath);
        Album = new MusicAlbum { Id = Guid.NewGuid(), ParentId = _libraryId, Path = Directory,
            Artists = ["Utada Hikaru"], AlbumArtists = ["Utada Hikaru", "宇多田ヒカル"] };
        Track = new Audio { Id = Guid.NewGuid(), ParentId = Album.Id, Path = audioPath,
            Artists = ["Utada Hikaru"], AlbumArtists = ["Utada Hikaru"] };
        Artist = new MusicArtist { Id = Guid.NewGuid(), Name = "宇多田ヒカル", Path = Path.Combine(Directory, "artist"),
            ProviderIds = new() { ["MusicBrainzArtist"] = "retained-provider-id" } };
        Store = new RecoveryStore(Directory);
        var library = StrictProxy.Create<ILibraryManager>((m, a) => m.Name switch
        {
            "get_IsScanRunning" => Scanning,
            "GetVirtualFolders" => new List<MediaBrowser.Model.Entities.VirtualFolderInfo> { new() { ItemId = _libraryId.ToString(), CollectionType = MediaBrowser.Model.Entities.CollectionTypeOptions.music } },
            "GetItemById" => (Guid)a![0]! == Album.Id ? Album : (Guid)a[0]! == Artist.Id ? Artist : Track,
            "GetItemList" => DuplicateIdentity ? new BaseItem[] { Artist, Artist } : new BaseItem[] { Artist },
            "GetItemIds" => ((InternalItemsQuery)a![0]!).IncludeItemTypes.Contains(BaseItemKind.MusicAlbum) ? new Guid[] { Album.Id } : new Guid[] { Track.Id },
            _ => throw new Exception("Unexpected API " + m.Name)
        });
        Library = library;
        var writer = StrictProxy.Create<ItemWriter>((m, a) =>
        {
            if (m.Name != "SaveItems") throw new Exception("Unexpected persistence call " + m.Name);
            if (Store.List().Count == 0) throw new Exception("Write before recovery backup!");
            if (FailWrite) throw new IOException("Simulated save failure");
            var items = (IReadOnlyList<BaseItem>)a![0]!;
            if (items.Any(i => i is not MusicAlbum && i is not MusicArtist && i is not Audio)) throw new Exception("Unexpected entity mutation");
            Writes++;
            return null;
        });
        Cleanup = new CleanupService(library, writer, Store, NullLogger<CleanupService>.Instance);
    }
    public AlbumChange? Plan() => Cleanup.Plan(Album, [Track.Id], new Dictionary<Guid, (string[], string[])>
    { [Track.Id] = (Track.Artists.ToArray(), Track.AlbumArtists.ToArray()) });
    public void Dispose() => System.IO.Directory.Delete(Directory, true);
}
