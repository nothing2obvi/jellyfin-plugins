using Jellyfin.Plugin.JellyTag.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTag.Tasks;

/// <summary>
/// Scheduled task that prebuilds JellyTag-Plus badge status data.
/// </summary>
public class BuildBadgeStatusIndexTask : IScheduledTask
{
    private static int _isRunning;
    private readonly IQualityDetectionService _qualityDetectionService;
    private readonly ILogger<BuildBadgeStatusIndexTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BuildBadgeStatusIndexTask"/> class.
    /// </summary>
    public BuildBadgeStatusIndexTask(IQualityDetectionService qualityDetectionService, ILogger<BuildBadgeStatusIndexTask> logger)
    {
        _qualityDetectionService = qualityDetectionService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "JellyTag-Plus Build Badge Status Index";

    /// <inheritdoc />
    public string Key => "JellyTagPlusBuildBadgeStatusIndex";

    /// <inheritdoc />
    public string Description => "Prebuilds the JellyTag-Plus badge status index used by image overlay requests.";

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
                IntervalTicks = TimeSpan.FromHours(6).Ticks
            }
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            progress.Report(100);
            _logger.LogInformation("JellyTag-Plus badge status index build skipped because another build is already running");
            return;
        }

        try
        {
            progress.Report(1);
            _logger.LogInformation("Building JellyTag-Plus badge status index");
            await _qualityDetectionService.RefreshBadgeStatusIndexAsync(
                percent => progress.Report(Math.Clamp(percent * 100, 1, 99)),
                cancellationToken).ConfigureAwait(false);
            progress.Report(100);
            _logger.LogInformation("Finished building JellyTag-Plus badge status index");
        }
        finally
        {
            Volatile.Write(ref _isRunning, 0);
        }
    }
}
