using Jellyfin.Plugin.JellyTag.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTag.Tasks;

/// <summary>
/// Scheduled task that prebuilds JellyTag-Plus collection membership data.
/// </summary>
public class BuildCollectionMembershipIndexTask : IScheduledTask
{
    private static int _isRunning;
    private readonly IQualityDetectionService _qualityDetectionService;
    private readonly ILogger<BuildCollectionMembershipIndexTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BuildCollectionMembershipIndexTask"/> class.
    /// </summary>
    public BuildCollectionMembershipIndexTask(IQualityDetectionService qualityDetectionService, ILogger<BuildCollectionMembershipIndexTask> logger)
    {
        _qualityDetectionService = qualityDetectionService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "JellyTag-Plus Build Collection Membership Index";

    /// <inheritdoc />
    public string Key => "JellyTagPlusBuildCollectionMembershipIndex";

    /// <inheritdoc />
    public string Description => "Prebuilds the JellyTag-Plus collection membership index used by collection badge checks.";

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
            _logger.LogInformation("JellyTag-Plus collection membership index build skipped because another build is already running");
            return;
        }

        try
        {
            progress.Report(1);
            _logger.LogInformation("Building JellyTag-Plus collection membership index");
            await _qualityDetectionService.RefreshCollectionMembershipIndexAsync(cancellationToken).ConfigureAwait(false);
            progress.Report(100);
            _logger.LogInformation("Finished building JellyTag-Plus collection membership index");
        }
        finally
        {
            Volatile.Write(ref _isRunning, 0);
        }
    }
}
