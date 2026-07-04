using Jellyfin.Plugin.JellyTag.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTag.Tasks;

/// <summary>
/// Scheduled task that calculates cached warmer progress for the configuration page.
/// </summary>
public class CalculateWarmerProgressTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILearnedClientProfileService _learnedClientProfileService;
    private readonly IImageCacheService _cacheService;
    private readonly ILogger<CacheWarmTask> _cacheWarmLogger;
    private readonly ILogger<CalculateWarmerProgressTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CalculateWarmerProgressTask"/> class.
    /// </summary>
    public CalculateWarmerProgressTask(
        ILibraryManager libraryManager,
        ILearnedClientProfileService learnedClientProfileService,
        IImageCacheService cacheService,
        ILogger<CacheWarmTask> cacheWarmLogger,
        ILogger<CalculateWarmerProgressTask> logger)
    {
        _libraryManager = libraryManager;
        _learnedClientProfileService = learnedClientProfileService;
        _cacheService = cacheService;
        _cacheWarmLogger = cacheWarmLogger;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "JellyTag-Plus Calculate Warmer Progress";

    /// <inheritdoc />
    public string Key => "JellyTagPlusCalculateWarmerProgress";

    /// <inheritdoc />
    public string Description => "Calculates cached JellyTag-Plus cache warmer progress for the plugin configuration page.";

    /// <inheritdoc />
    public string Category => "JellyTag-Plus";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.Enabled)
        {
            progress.Report(100);
            _logger.LogInformation("JellyTag-Plus warmer progress calculation skipped because the plugin is disabled");
            return;
        }

        if (CacheWarmTask.IsProgressCalculationRunning)
        {
            progress.Report(100);
            _logger.LogInformation("JellyTag-Plus warmer progress calculation skipped because another calculation is already running");
            return;
        }

        _logger.LogInformation("Calculating JellyTag-Plus warmer progress");
        progress.Report(1);
        await CacheWarmTask.CalculateAndCacheEstimatedClientProgressAsync(config, _libraryManager, _learnedClientProfileService, _cacheService, _cacheWarmLogger).ConfigureAwait(false);
        progress.Report(100);
        _logger.LogInformation("Finished calculating JellyTag-Plus warmer progress");
    }
}
