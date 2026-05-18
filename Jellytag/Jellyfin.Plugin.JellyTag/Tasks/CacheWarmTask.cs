using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyTag.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTag.Tasks;

public class CacheWarmTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<CacheWarmTask> _logger;

    public CacheWarmTask(ILibraryManager libraryManager, IServerApplicationHost applicationHost, ILogger<CacheWarmTask> logger)
    {
        _libraryManager = libraryManager;
        _applicationHost = applicationHost;
        _logger = logger;
    }

    public string Name => "JellyTag-Plus Cache Warmer";
    public string Key => "JellyTagPlusCacheWarmer";
    public string Description => "Pre-renders JellyTag-Plus poster and thumbnail overlays for enabled libraries.";
    public string Category => "JellyTag-Plus";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.Enabled) { progress.Report(100); return; }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode, BaseItemKind.Video]
        }).Where(item => IsInEnabledLibrary(item, config)).ToList();

        var requests = new List<(Guid ItemId, string ImageType)>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldRequestPrimary(item, config) && item.HasImage(ImageType.Primary, 0)) requests.Add((item.Id, "Primary"));
            if (ShouldRequestThumb(item, config) && item.HasImage(ImageType.Thumb, 0)) requests.Add((item.Id, "Thumb"));
        }

        if (requests.Count == 0) { progress.Report(100); return; }

        var baseUrl = GetBaseUrl();
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };

        var completed = 0;
        var warmed = 0;
        var failed = 0;
        using var throttler = new SemaphoreSlim(3, 3);

        var tasks = requests.Select(async request =>
        {
            await throttler.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var url = $"{baseUrl}/Items/{request.ItemId:N}/Images/{request.ImageType}?jellytagwarm=1";
                using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) Interlocked.Increment(ref warmed);
                else
                {
                    Interlocked.Increment(ref failed);
                    _logger.LogDebug("JellyTag-Plus cache warmer got {StatusCode} for {Url}", response.StatusCode, url);
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                _logger.LogDebug(ex, "JellyTag-Plus cache warmer failed for {ItemId} {ImageType}", request.ItemId, request.ImageType);
            }
            finally
            {
                var done = Interlocked.Increment(ref completed);
                progress.Report(done * 100.0 / requests.Count);
                throttler.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        _logger.LogInformation("JellyTag-Plus cache warmer complete. Requested {Total}, warmed {Warmed}, failed {Failed}", requests.Count, warmed, failed);
    }

    private string GetBaseUrl()
    {
        var scheme = _applicationHost.ListenWithHttps ? "https" : "http";
        var port = _applicationHost.ListenWithHttps ? _applicationHost.HttpsPort : _applicationHost.HttpPort;
        var url = _applicationHost.GetLocalApiUrl("127.0.0.1", scheme, port);
        return string.IsNullOrWhiteSpace(url) ? $"{scheme}://127.0.0.1:{port}" : url.TrimEnd('/');
    }

    private bool IsInEnabledLibrary(BaseItem item, PluginConfiguration config)
    {
        var folders = _libraryManager.GetCollectionFolders(item).ToList();
        if (folders.Count == 0) return true;
        if (config.ExcludedLibraryIds?.Count > 0 && folders.Any(f => config.ExcludedLibraryIds.Contains(f.Id.ToString("N")))) return false;
        return true;
    }

    private static bool ShouldRequestPrimary(BaseItem item, PluginConfiguration config)
    {
        if (item is Episode) return config.ThumbnailSameAsPoster ? config.PosterConfig.Enabled : config.ThumbnailConfig.Enabled;
        return config.PosterConfig.Enabled;
    }

    private static bool ShouldRequestThumb(BaseItem item, PluginConfiguration config)
    {
        return config.ThumbnailSameAsPoster ? config.PosterConfig.Enabled : config.ThumbnailConfig.Enabled;
    }
}
