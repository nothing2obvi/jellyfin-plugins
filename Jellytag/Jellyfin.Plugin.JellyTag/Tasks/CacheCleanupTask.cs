using Jellyfin.Plugin.JellyTag.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTag.Tasks;

/// <summary>
/// Scheduled task that removes expired JellyTag image cache files.
/// </summary>
public class CacheCleanupTask : IScheduledTask
{
    private readonly IImageCacheService _cacheService;
    private readonly ILogger<CacheCleanupTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CacheCleanupTask"/> class.
    /// </summary>
    public CacheCleanupTask(IImageCacheService cacheService, ILogger<CacheCleanupTask> logger)
    {
        _cacheService = cacheService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "JellyTag-Plus Cache Cleanup";

    /// <inheritdoc />
    public string Key => "JellyTagCacheCleanup";

    /// <inheritdoc />
    public string Description => "Removes expired JellyTag-Plus image cache files";

    /// <inheritdoc />
    public string Category => "JellyTag-Plus";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(24).Ticks
            }
        };
    }

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var cacheDir = _cacheService.GetCacheDirectory();
        if (!Directory.Exists(cacheDir))
        {
            _logger.LogDebug("Cache directory does not exist, nothing to clean");
            return Task.CompletedTask;
        }

        var config = Plugin.Instance?.Configuration;
        var cacheHours = config?.CacheDurationHours ?? 168;
        if (cacheHours <= 0)
        {
            _logger.LogInformation("JellyTag cache cleanup skipped because cache duration is set to forever");
            progress.Report(100);
            return Task.CompletedTask;
        }

        var deletedCount = 0;

        var files = Directory.GetFiles(cacheDir, "*.jpg", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(cacheDir, "*.webp", SearchOption.AllDirectories))
            .ToArray();

        if (files.Length == 0)
        {
            _cacheService.PruneCacheIndex();
            progress.Report(100);
            return Task.CompletedTask;
        }

        for (int i = 0; i < files.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var fileInfo = new FileInfo(files[i]);
                if ((DateTime.UtcNow - fileInfo.LastWriteTimeUtc).TotalHours > cacheHours)
                {
                    fileInfo.Delete();
                    deletedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete cache file: {Path}", files[i]);
            }

            progress.Report((double)(i + 1) / files.Length * 100);
        }

        _cacheService.PruneCacheIndex();
        _logger.LogInformation("JellyTag cache cleanup complete. Deleted {Count} expired files", deletedCount);
        return Task.CompletedTask;
    }
}
