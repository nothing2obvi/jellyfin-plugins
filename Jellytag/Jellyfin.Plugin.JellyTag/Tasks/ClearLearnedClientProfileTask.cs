using Jellyfin.Plugin.JellyTag.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTag.Tasks;

/// <summary>
/// Scheduled task that clears learned client warmer variants.
/// </summary>
public class ClearLearnedClientProfileTask : IScheduledTask
{
    private readonly ILearnedClientProfileService _learnedClientProfileService;
    private readonly ILogger<ClearLearnedClientProfileTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClearLearnedClientProfileTask"/> class.
    /// </summary>
    public ClearLearnedClientProfileTask(ILearnedClientProfileService learnedClientProfileService, ILogger<ClearLearnedClientProfileTask> logger)
    {
        _learnedClientProfileService = learnedClientProfileService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "JellyTag-Plus Clear Learned Client Profile";

    /// <inheritdoc />
    public string Key => "JellyTagPlusClearLearnedClientProfile";

    /// <inheritdoc />
    public string Description => "Clears the JellyTag-Plus learned client warmer profile.";

    /// <inheritdoc />
    public string Category => "JellyTag-Plus";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _learnedClientProfileService.Clear();
        progress.Report(100);
        _logger.LogInformation("Cleared JellyTag-Plus learned client warmer profile");
        return Task.CompletedTask;
    }
}
