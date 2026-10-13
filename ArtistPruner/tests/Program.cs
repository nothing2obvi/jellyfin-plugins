using System.Reflection;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ArtistPruner.Configuration;
using Jellyfin.Plugin.ArtistPruner.Services;
using Jellyfin.Plugin.ArtistPruner.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;

BaseItem.MediaSourceManager = StrictProxy.Create<IMediaSourceManager>((method, args) =>
    method.Name == "GetPathProtocol" ? MediaProtocol.File : throw new Exception("Unexpected media source call"));
var passed = 0;
void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
    Console.WriteLine("PASS " + message);
    passed++;
}
async Task Throws<T>(Func<Task> action, string message) where T : Exception
{
    try { await action(); } catch (T) { Check(true, message); return; }
    throw new Exception("Did not throw: " + message);
}
Check(!CreditComparison.SameCredits(["Utada Hikaru"], ["宇多田ヒカル"]), "alternate-language credits remain distinct");
Check(!CreditComparison.SameCredits(["BAND-MAID"], ["BAND‐MAID"]), "Unicode dash credits remain distinct");
Check(!CreditComparison.SameCredits(["Meg"], ["MEG"]), "capitalization remains distinct");
Check(!CreditComparison.SameCredits(["é"], ["e\u0301"]), "Unicode normalization is not forced");
Check(CreditComparison.SameCredits(["A", "B"], ["B", "A"]), "collaborations compare as sets");
Check(!CreditComparison.SameCredits(["A", "B"], ["A"]), "missing collaborator is a discrepancy");
Check(CreditComparison.SameCredits(["Various Artists"], ["Various Artists"]), "compilation credit is preserved");
Check(!CreditComparison.HasUsableCredits([]) && !CreditComparison.HasUsableCredits([" "]), "missing and malformed credits are not usable");
using var fixture = new Fixture(205);
var progress = new List<double>();
var result = await fixture.Service.PruneAsync(new InlineProgress(progress.Add), default);
Check(result.TracksInspected == 205 && fixture.ProbeCalls == 205, "all items beyond old 100-item boundary probed once");
Check(result.AlbumsInspected == 1 && result.AlbumCreditMismatches == 1, "extra album artist reported without changing album");
Check(result.CreditMismatches == 0 && result.EntriesRemoved == 0 && result.AssociationsCorrected == 0, "matching dry run never mutates");
Check(result.ArtistsInspected == 4 && result.UnsupportedNames == 1, "album-only artist inspected; unscoped orphan excluded");
Check(progress.Last() == 100 && progress.All(p => p >= 0 && p <= 100), "Jellyfin progress uses 0–100");
Check(fixture.Artists[0].ProviderIds["MusicBrainzArtist"] != fixture.Artists[1].ProviderIds["MusicBrainzArtist"], "same-name distinct provider identities unchanged");
await fixture.Service.PruneAsync(null, default);
Check(fixture.ProbeCalls == 410 && fixture.TrackIds.Count == 205, "repeated runs preserve records and relationships");
Check(File.ReadAllText(fixture.Path) == "unchanged fixture", "media file content unchanged");
fixture.Config.DryRun = false;
await Throws<InvalidOperationException>(() => fixture.Service.PruneAsync(null, default), "full mode without recovery service fails closed");
Check(fixture.ProbeCalls == 410, "unsafe request rejected before probing");
fixture.Config.DryRun = true;
fixture.Config.Enabled = false;
await fixture.Service.PruneAsync(null, default);
Check(fixture.ProbeCalls == 410, "disabled plugin does no work");
fixture.Config.Enabled = true;
fixture.Scanning = true;
await Throws<InvalidOperationException>(() => fixture.Service.PruneAsync(null, default), "active library scan blocks audit");
fixture.Scanning = false;
using (var cts = new CancellationTokenSource())
{
    cts.Cancel();
    await Throws<OperationCanceledException>(() => fixture.Service.PruneAsync(null, cts.Token), "cancellation before start propagates");
}
using var small = new Fixture(1);
small.Probe = _ => Task.FromResult(new MediaInfo { Artists = ["BAND‐MAID"], AlbumArtists = ["Various Artists"] });
var mismatch = await small.Service.PruneAsync(null, default);
Check(mismatch.CreditMismatches == 1 && mismatch.EntriesRemoved == 0, "Unicode mismatch reported without deletion");
small.Probe = _ => throw new IOException("Malformed input");
Check((await small.Service.PruneAsync(null, default)).TracksSkipped == 1, "probe failure is inconclusive and preserved");
small.Probe = _ => throw new OperationCanceledException();
Check((await small.Service.PruneAsync(null, default)).TracksSkipped == 1, "probe timeout is inconclusive");
small.Probe = _ => Task.FromResult(new MediaInfo());
var missing = await small.Service.PruneAsync(null, default);
Check(missing.TracksSkipped == 1 && missing.AlbumsSkipped == 1, "empty metadata makes album evidence incomplete");
small.Probe = _ => { small.Scanning = true; return Task.FromResult(new MediaInfo()); };
await Throws<InvalidOperationException>(() => small.Service.PruneAsync(null, default), "scan starting during probe aborts audit");
small.Scanning = false;
var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
small.Probe = async token => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new MediaInfo(); };
using (var cts = new CancellationTokenSource())
{
    var running = small.Service.PruneAsync(null, cts.Token);
    await started.Task;
    await Throws<InvalidOperationException>(() => small.Service.PruneAsync(null, default), "overlapping run rejected");
    cts.Cancel();
    await Throws<OperationCanceledException>(() => running, "in-flight cancellation propagates");
}
small.Probe = _ => Task.FromResult(new MediaInfo { Artists = ["BAND-MAID"], AlbumArtists = ["Various Artists"] });
Check((await small.Service.PruneAsync(null, default)).TracksInspected == 1, "gate released after cancellation and scan failure");
File.Delete(small.Path);
Check((await small.Service.PruneAsync(null, default)).TracksSkipped == 1, "missing file does not justify deletion");
var task = new PruneArtistsTask(small.Service);
Check(task.Key == "ArtistPruner_PruneMusicArtists" && !task.GetDefaultTriggers().Any(), "native task keeps stable key and has no automatic default schedule");
await FullModeTests.Run(Check);
Console.WriteLine($"{passed} checks passed. Interface fakes reject all unanticipated calls, including unauthorized writes.");

sealed class InlineProgress(Action<double> callback) : IProgress<double>
{
    public void Report(double value) => callback(value);
}

public class StrictProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    public static T Create<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var instance = DispatchProxy.Create<T, StrictProxy>();
        ((StrictProxy)(object)instance).Handler = handler;
        return instance;
    }
}

sealed class Fixture : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "artistpruner-test-" + Guid.NewGuid());
    public Guid AlbumId { get; } = Guid.NewGuid();
    public Guid LibraryId { get; } = Guid.NewGuid();
    public List<Guid> TrackIds { get; } = [];
    public List<MusicArtist> Artists { get; } = [];
    public PluginConfiguration Config { get; } = new();
    public bool Scanning { get; set; }
    public int ProbeCalls { get; private set; }
    public Func<CancellationToken, Task<MediaInfo>> Probe { get; set; } = _ => Task.FromResult(new MediaInfo
    {
        Artists = ["BAND-MAID"], AlbumArtists = ["Various Artists"]
    });
    public ArtistPruneService Service { get; }
    public Fixture(int count)
    {
        File.WriteAllText(Path, "unchanged fixture");
        var items = new Dictionary<Guid, BaseItem>();
        for (var i = 0; i < count; i++)
        {
            var audio = new Audio { Id = Guid.NewGuid(), Path = Path, Artists = ["BAND-MAID"], AlbumArtists = ["Various Artists"] };
            items[audio.Id] = audio; TrackIds.Add(audio.Id);
        }
        foreach (var name in new[] { "BAND-MAID", "BAND-MAID", "Various Artists", "宇多田ヒカル", "Unscoped orphan" })
        {
            var artist = new MusicArtist { Id = Guid.NewGuid(), Name = name, ProviderIds = new() { ["MusicBrainzArtist"] = Guid.NewGuid().ToString() } };
            Artists.Add(artist); items[artist.Id] = artist;
        }
        items[AlbumId] = new MusicAlbum { Id = AlbumId, Artists = ["BAND-MAID"], AlbumArtists = ["Various Artists", "Extra provider credit"] };
        var library = StrictProxy.Create<ILibraryManager>((method, args) => method.Name switch
        {
            "get_IsScanRunning" => Scanning,
            "GetVirtualFolders" => new List<VirtualFolderInfo> { new() { ItemId = LibraryId.ToString(), CollectionType = CollectionTypeOptions.music }, new() { ItemId = Guid.NewGuid().ToString(), CollectionType = CollectionTypeOptions.movies } },
            "GetItemById" => items[(Guid)args![0]!],
            "GetItemIds" => Query((InternalItemsQuery)args![0]!),
            _ => throw new Exception("Unexpected library API (including any mutation): " + method.Name)
        });
        var encoder = StrictProxy.Create<IMediaEncoder>((method, args) =>
        {
            if (method.Name != "GetMediaInfo") throw new Exception("Unexpected encoder API: " + method.Name);
            var request = (MediaInfoRequest)args![0]!;
            if (request.MediaSource.Path != Path || request.ExtractChapters) throw new Exception("Bad probe request");
            ProbeCalls++;
            return Probe((CancellationToken)args[1]!);
        });
        Service = new ArtistPruneService(library, encoder, NullLogger<ArtistPruneService>.Instance, () => Config);
    }
    private IReadOnlyList<Guid> Query(InternalItemsQuery query)
    {
        if (!query.GroupByPresentationUniqueKey && query.IncludeItemTypes.SequenceEqual(new[] { BaseItemKind.MusicArtist }) && query.AncestorIds.Length == 0)
            return Artists.Select(a => a.Id).ToArray();
        if (query.AncestorIds.SequenceEqual(new[] { AlbumId }) && query.IncludeItemTypes.SequenceEqual(new[] { BaseItemKind.Audio })) return TrackIds;
        if (!query.AncestorIds.SequenceEqual(new[] { LibraryId })) throw new Exception("Query escaped music library scope");
        if (query.IncludeItemTypes.SequenceEqual(new[] { BaseItemKind.MusicAlbum })) return [AlbumId];
        if (query.ItemIds.Length > 0) return [];
        if (query.ArtistIds.Length > 0 || query.AlbumArtistIds.Length > 0)
        {
            if (query.Limit != 1) throw new Exception("Relationship query should be bounded");
            var id = query.ArtistIds.Concat(query.AlbumArtistIds).Single();
            if (id == Artists[4].Id) return [];
            if (id == Artists[3].Id && query.ArtistIds.Length > 0) return [];
            return [TrackIds[0]];
        }
        if (query.IncludeItemTypes.SequenceEqual(new[] { BaseItemKind.Audio })) return TrackIds;
        throw new Exception("Unrecognized query");
    }
    public void Dispose() => File.Delete(Path);
}
