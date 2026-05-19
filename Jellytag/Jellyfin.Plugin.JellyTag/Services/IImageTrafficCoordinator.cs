namespace Jellyfin.Plugin.JellyTag.Services;

/// <summary>
/// Coordinates warmer image requests with normal client image traffic.
/// </summary>
public interface IImageTrafficCoordinator
{
    /// <summary>
    /// Records a normal client image request.
    /// </summary>
    void NotifyClientImageRequest();

    /// <summary>
    /// Waits until no normal client image requests have been seen for the configured quiet period.
    /// </summary>
    Task WaitForClientQuietPeriodAsync(TimeSpan quietPeriod, CancellationToken cancellationToken);
}
