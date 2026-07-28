using Jellyfin.Plugin.JellyTag.Services;
using Jellyfin.Plugin.JellyTag.Middleware;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTag.Tasks;

/// <summary>
/// Scheduled task that prebuilds JellyTag-Plus badge status data.
/// </summary>
public class BuildBadgeStatusIndexTask : IScheduledTask
{
    private static int _isRunning;
    private readonly IBadgeVisibilityService _badgeVisibilityService;
    private readonly IProviderManager _providerManager;
    private readonly ILogger<BuildBadgeStatusIndexTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BuildBadgeStatusIndexTask"/> class.
    /// </summary>
    public BuildBadgeStatusIndexTask(IBadgeVisibilityService badgeVisibilityService, IProviderManager providerManager, ILogger<BuildBadgeStatusIndexTask> logger)
    {
        _badgeVisibilityService = badgeVisibilityService;
        _providerManager = providerManager;
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
            await _badgeVisibilityService.RefreshBadgeStatusIndexAsync(
                percent => progress.Report(Math.Clamp(percent * 100, 1, 99)),
                async (item, imageType, state, forceMetadataRefresh, token) =>
                {
                    await ImageOverlayMiddleware.TryForceImageRefreshForStateAsync(item, imageType, state.BadgeState, state.HasVisibleBadges, _providerManager, _logger, forceMetadataRefresh, token).ConfigureAwait(false);
                },
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
