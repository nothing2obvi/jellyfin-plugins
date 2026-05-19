namespace Jellyfin.Plugin.JellyTag.Services;

/// <summary>
/// Shared traffic signal used to let real client browsing take priority over cache warming.
/// </summary>
public class ImageTrafficCoordinator : IImageTrafficCoordinator
{
    private readonly object _lock = new();
    private DateTime _lastClientImageRequestUtc = DateTime.MinValue;

    /// <inheritdoc />
    public void NotifyClientImageRequest()
    {
        lock (_lock)
        {
            _lastClientImageRequestUtc = DateTime.UtcNow;
        }
    }

    /// <inheritdoc />
    public async Task WaitForClientQuietPeriodAsync(TimeSpan quietPeriod, CancellationToken cancellationToken)
    {
        if (quietPeriod <= TimeSpan.Zero)
        {
            return;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DateTime lastRequestUtc;
            lock (_lock)
            {
                lastRequestUtc = _lastClientImageRequestUtc;
            }

            var quietFor = DateTime.UtcNow - lastRequestUtc;
            if (quietFor >= quietPeriod)
            {
                return;
            }

            var remaining = quietPeriod - quietFor;
            var delay = remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }
}
