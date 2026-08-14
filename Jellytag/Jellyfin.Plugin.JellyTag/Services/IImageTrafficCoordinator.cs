namespace Jellyfin.Plugin.JellyTag.Services;

/// <summary>
/// Coordinates warmer image requests with normal client image traffic.
/// </summary>
public interface IImageTrafficCoordinator
{
    /// <summary>
    /// Records a normal client image request without tracking active work.
    /// </summary>
    void NotifyClientImageRequest();

    /// <summary>
    /// Records active normal client image work until the returned scope is disposed.
    /// </summary>
    IDisposable BeginClientImageActivity();

    /// <summary>
    /// Waits until no normal client image activity has been active or seen for the configured quiet period.
    /// </summary>
    Task WaitForClientQuietPeriodAsync(TimeSpan quietPeriod, CancellationToken cancellationToken);
}
