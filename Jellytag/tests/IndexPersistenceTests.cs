using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.JellyTag.Configuration;
using Jellyfin.Plugin.JellyTag.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging.Abstractions;

internal static class IndexPersistenceTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Writer(object service) => service.GetType().GetField("_indexWriter", Private)!.GetValue(service)!;
    private static void Flush(object service)
    {
        var writer = Writer(service);
        writer.GetType().GetMethod("Flush", Private)!.Invoke(writer, null);
    }
    private static void DisableTimer(object service)
    {
        var writer = Writer(service);
        ((Timer)writer.GetType().GetField("_timer", Private)!.GetValue(writer)!).Change(Timeout.Infinite, Timeout.Infinite);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static int Count(string path, string property)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty(property).EnumerateObject().Count();
    }
    private static ImageCacheService Cache(string folder)
    {
        var service = new ImageCacheService(NullLogger<ImageCacheService>.Instance);
        DisableTimer(service);
        typeof(ImageCacheService).GetField("_cachePath", Private)!.SetValue(service, folder);
        return service;
    }
    internal static async Task Run(string root)
    {
        var folder = Path.Combine(root, "index-cache");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "cache-index.json");
        using var service = Cache(folder);
        var item = Guid.NewGuid();
        using (var image = new MemoryStream(new byte[] { 1, 2, 3 }))
            Check(await service.CacheImageAsync(item, "badge", "image", "state", image), "Image caching failed");
        Parallel.For(0, 100, i => service.SetRequestCacheEntry("request" + i, item, "badge", "image", "state"));
        Check(!File.Exists(path), "Request path wrote the cache index");
        Check(await service.GetCachedImageFileForRequestAsync(item, "request0") != null, "Pending index entry not immediately visible");
        Flush(service);
        Check(Count(path, "Entries") == 1 && Count(path, "RequestEntries") == 100, "Cache index batch lost entries");
        using (var reloaded = Cache(folder))
            Check(await reloaded.GetCachedImageFileForRequestAsync(item, "request0") != null, "Persisted cache shortcut failed to reload");

        // Simulate a slow writer holding the disk lock; requests must still finish.
        var writer = Writer(service);
        var diskLock = writer.GetType().GetProperty("PersistenceLock", Private)!.GetValue(writer)!;
        lock (diskLock)
        {
            Check(Task.Run(() => service.SetRequestCacheEntry("during-write", item, "badge", "image", "state")).Wait(TimeSpan.FromSeconds(5)), "Cache requests blocked on persistence");
        }
        Parallel.Invoke(
            () => Parallel.For(100, 200, i => service.SetRequestCacheEntry("request" + i, item, "badge", "image", "state")),
            () => { for (var i = 0; i < 10; i++) Flush(service); });
        Flush(service);
        Check(Count(path, "RequestEntries") == 201, "Concurrent flush lost request entries");
        Parallel.Invoke(() => Flush(service), () => service.ClearCache());
        Flush(service);
        Check(!File.Exists(path), "Pending flush resurrected cleared cache");
        Check(await service.GetCachedImageFileForRequestAsync(item, "request0") == null, "Clear retained in-memory shortcuts");

        // A publication failure must retain pending changes and clean its temporary file.
        Directory.CreateDirectory(path);
        service.SetRequestCacheEntry("retry", item, "badge", "image", "state");
        Flush(service);
        Check(Directory.GetFiles(folder, "cache-index.json.*.tmp").Length == 0, "Failed flush leaked a temporary file");
        Directory.Delete(path);
        Flush(service);
        Check(Count(path, "RequestEntries") == 1, "Failed index write was not retried");
        service.RemoveRequestCacheEntry("retry");
        service.Dispose();
        Check(Count(path, "RequestEntries") == 0, "Shutdown failed to persist removal");

        TestSnapshotRace(root);
        TestBadges(root);
        Console.WriteLine("PASS: index batching, immediate hits, reload, concurrent flush, disk-lock independence, clear, retry, shutdown, badge replacement");
    }

    private static void TestSnapshotRace(string root)
    {
        var path = Path.Combine(root, "snapshot-race.json");
        var writerType = typeof(ImageCacheService).Assembly.GetType("Jellyfin.Plugin.JellyTag.Services.BatchedJsonWriter`1")!
            .MakeGenericType(typeof(Dictionary<string, int>));
        using var captured = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var value = 1;
        var snapshots = 0;
        Func<Dictionary<string, int>> snapshot = () =>
        {
            var copy = new Dictionary<string, int> { ["value"] = Volatile.Read(ref value) };
            if (Interlocked.Increment(ref snapshots) == 1)
            {
                captured.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Snapshot test timed out");
            }
            return copy;
        };
        using var writer = (IDisposable)Activator.CreateInstance(writerType, Private, null,
            new object[] { (Func<string?>)(() => path), snapshot, NullLogger.Instance }, null)!;
        var timer = (Timer)writerType.GetField("_timer", Private)!.GetValue(writer)!;
        timer.Change(Timeout.Infinite, Timeout.Infinite);
        void Dirty() => writerType.GetMethod("MarkDirty", Private)!.Invoke(writer, null);
        void Save() => writerType.GetMethod("Flush", Private)!.Invoke(writer, null);
        Dirty();
        var saving = Task.Run(Save);
        try
        {
            Check(captured.Wait(TimeSpan.FromSeconds(5)), "Snapshot did not start");
            Volatile.Write(ref value, 2);
            Dirty();
        }
        finally { release.Set(); }
        Check(saving.Wait(TimeSpan.FromSeconds(5)), "Snapshot did not finish");
        Save();
        Check(JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path))!["value"] == 2,
            "An update arriving after snapshot capture was lost");
        Save();
        Check(snapshots == 2, "Clean index was unnecessarily snapshotted again");

        Volatile.Write(ref value, 3);
        Dirty();
        timer.Change(1, Timeout.Infinite);
        Check(SpinWait.SpinUntil(() => Volatile.Read(ref snapshots) == 3, TimeSpan.FromSeconds(5)), "Background timer failed to flush");
        writer.Dispose(); // Joins persistence through its lock before reading the file.
        Check(JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path))!["value"] == 3,
            "Background flush did not publish its snapshot");
    }

    private static void TestBadges(string root)
    {
        var path = Path.Combine(root, "badge-index.json");
        using var service = new BadgeVisibilityService(null!, null!, null!, NullLogger<BadgeVisibilityService>.Instance);
        DisableTimer(service);
        var writer = Writer(service);
        writer.GetType().GetField("_getPath", Private)!.SetValue(writer, (Func<string?>)(() => path));
        var config = new PluginConfiguration();
        var movie = new Movie { Id = Guid.NewGuid() };
        var upsert = typeof(BadgeVisibilityService).GetMethod("UpsertVisibleBadgeState", Private)!;
        void Update(string state) => upsert.Invoke(service, new object[] { movie, "Primary", "1", config, Array.Empty<Folder>(), "config", new VisibleBadgeState([], state, "badge", true) });
        Update("old");
        Check(!File.Exists(path), "Badge request wrote index synchronously");
        Check(service.TryGetIndexedVisibleBadgeState(movie, "Primary", "1", config, [], "config")?.BadgeState == "old", "Badge update not immediately visible");
        Parallel.Invoke(() => { for (var i = 0; i < 100; i++) Update(i.ToString()); }, () => { for (var i = 0; i < 10; i++) Flush(service); });
        Update("latest");
        Flush(service);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Check(json.RootElement.GetProperty("Entries").EnumerateObject().Single().Value.GetProperty("BadgeState").GetString() == "latest", "Badge flush lost latest state");

        var replace = typeof(BadgeVisibilityService).GetMethod("ReplaceVisibleBadgeStateIndex", Private)!;
        var entries = Activator.CreateInstance(replace.GetParameters()[0].ParameterType)!;
        replace.Invoke(service, new[] { entries });
        service.Dispose();
        Check(Count(path, "Entries") == 0, "Full badge-index replacement retained stale entries");
    }
}
