using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Plugin.JellyTag.Configuration;
using Jellyfin.Plugin.JellyTag.Services;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using SkiaSharp;

var root = Path.Combine(Path.GetTempPath(), "jellytag-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    TestProfilePersistence();
    await TestRendering();
    Console.WriteLine("PASS: all Jellytag regression checks");
}
finally { Directory.Delete(root, recursive: true); }

void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

LearnedClientProfileService Profile(string path)
{
    var service = new LearnedClientProfileService(NullLogger<LearnedClientProfileService>.Instance);
    typeof(LearnedClientProfileService).GetField("_statePath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, path);
    var timer = (Timer)typeof(LearnedClientProfileService).GetField("_flushTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
    timer.Change(Timeout.Infinite, Timeout.Infinite);
    return service;
}

void Flush(LearnedClientProfileService service) => typeof(LearnedClientProfileService)
    .GetMethod("FlushPendingChanges", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, null);

void Record(LearnedClientProfileService service)
{
    var query = new QueryCollection(new Dictionary<string, StringValues> { ["width"] = "1230", ["height"] = "1870", ["quality"] = "83" });
    service.RecordVariant(new Movie(), "Primary", query, new HeaderDictionary(), new ClaimsPrincipal(), null, null);
}

int SavedCount(string path)
{
    using var json = JsonDocument.Parse(File.ReadAllText(path));
    return json.RootElement.GetProperty("Variants").EnumerateObject().Single().Value.GetProperty("SeenCount").GetInt32();
}

void TestProfilePersistence()
{
    var path = Path.Combine(root, "profile.json");
    using var service = Profile(path);
    Parallel.For(0, 200, _ => Record(service));
    Check(service.GetVariantInfo().Single().SeenCount == 200, "Concurrent learning lost updates");
    Check(!File.Exists(path), "Learning performed request-time persistence");
    Flush(service);
    Check(SavedCount(path) == 200, "Batch flush did not persist counters");
    using (var reloaded = Profile(path)) Check(reloaded.GetVariantInfo().Single().SeenCount == 200, "Persisted profile failed to reload");

    Parallel.Invoke(() => Parallel.For(0, 200, _ => Record(service)), () => { for (var i = 0; i < 20; i++) Flush(service); });
    Flush(service);
    Check(SavedCount(path) == 400, "Concurrent flush lost learning updates");
    Record(service);
    Parallel.Invoke(() => Flush(service), () => service.Clear());
    Flush(service);
    Check(!File.Exists(path) && service.GetVariants().Count == 0, "Flush resurrected cleared profile");
    Record(service);
    service.Dispose();
    Check(SavedCount(path) == 1, "Shutdown did not flush pending data");

    var blocked = Path.Combine(root, "blocked");
    File.WriteAllText(blocked, "block directory creation");
    using var retry = Profile(Path.Combine(blocked, "profile.json"));
    Record(retry);
    Flush(retry);
    File.Delete(blocked);
    Flush(retry);
    Check(SavedCount(Path.Combine(blocked, "profile.json")) == 1, "Failed persistence was not retried");
    Console.WriteLine("PASS: batching, concurrent updates/flush, reload, clear, shutdown, retry");
}

async Task TestRendering()
{
    using var renderer = new ImageOverlayService(NullLogger<ImageOverlayService>.Instance);
    var config = new ImageTypeConfig();
    config.ResolutionPanel.Enabled = true;
    config.ResolutionPanel.Style = BadgeStyle.Image;
    var badges = new List<BadgeInfo> { new() { Category = BadgeCategory.Resolution, BadgeKey = "4k", ResourceFileName = "badge-4k.svg" } };
    async Task<byte[]> Render(int width)
    {
        using var source = new SKBitmap(width, width * 3 / 2);
        source.Erase(SKColors.CornflowerBlue);
        using var original = SKImage.FromBitmap(source);
        using var encoded = original.Encode(SKEncodedImageFormat.Jpeg, 90);
        using var input = encoded.AsStream();
        var (stream, type) = await renderer.AddBadgeOverlaysAsync(input, badges, config);
        using (stream)
        {
            Check(type == "image/jpeg" && stream.CanSeek && stream.Length > 0, "Encoded stream is unusable");
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            stream.Position = 0;
            using var secondCopy = new MemoryStream();
            await stream.CopyToAsync(secondCopy);
            Check(copy.ToArray().SequenceEqual(secondCopy.ToArray()), "Cache-write/client-transfer stream reuse failed");
            using var output = SKBitmap.Decode(copy.ToArray());
            Check(output.Width == width && output.Height == width * 3 / 2, "Output dimensions changed");
            return copy.ToArray();
        }
    }
    var first = await Render(320);
    await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => Render(320 + i * 10))));
    var repeated = await Render(320);
    Check(first.SequenceEqual(repeated), "Repeat rendering changed pixels");
    var parsed = (System.Collections.IDictionary)typeof(ImageOverlayService).GetField("_parsedSvgCache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer)!;
    Check(parsed.Count == 1, "SVG was not shared across output sizes");
    renderer.ReloadBadges();
    Check(parsed.Count == 0, "Badge reload retained stale parsed SVGs");
    var reloaded = await Render(320);
    Check(first.SequenceEqual(reloaded), "Reload changed rendering");
    Console.WriteLine("PASS: concurrent rendering, SVG reuse/reload, seekable encoded-stream lifetime, stable output");
}
