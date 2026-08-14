namespace Jellyfin.Plugin.JellyTag.Services;

/// <summary>
/// Shared traffic signal used to let real client browsing take priority over cache warming.
/// </summary>
public class ImageTrafficCoordinator : IImageTrafficCoordinator
{
    private readonly object _lock = new();
    private DateTime _lastClientImageActivityUtc = DateTime.MinValue;
    private int _activeClientImageRequests;

    /// <inheritdoc />
    public void NotifyClientImageRequest()
    {
        lock (_lock)
        {
            _lastClientImageActivityUtc = DateTime.UtcNow;
        }
    }

    /// <inheritdoc />
    public IDisposable BeginClientImageActivity()
    {
        lock (_lock)
        {
            _activeClientImageRequests++;
            _lastClientImageActivityUtc = DateTime.UtcNow;
        }

        return new ClientImageActivityScope(this);
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

            DateTime lastActivityUtc;
            int activeRequests;
            lock (_lock)
            {
                lastActivityUtc = _lastClientImageActivityUtc;
                activeRequests = _activeClientImageRequests;
            }

            var quietFor = DateTime.UtcNow - lastActivityUtc;
            if (activeRequests == 0 && quietFor >= quietPeriod)
            {
                return;
            }

            var remaining = activeRequests > 0 ? TimeSpan.FromSeconds(1) : quietPeriod - quietFor;
            var delay = remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private void EndClientImageActivity()
    {
        lock (_lock)
        {
            if (_activeClientImageRequests > 0)
            {
                _activeClientImageRequests--;
            }

            _lastClientImageActivityUtc = DateTime.UtcNow;
        }
    }

    private sealed class ClientImageActivityScope : IDisposable
    {
        private readonly ImageTrafficCoordinator _coordinator;
        private int _disposed;

        public ClientImageActivityScope(ImageTrafficCoordinator coordinator)
        {
            _coordinator = coordinator;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _coordinator.EndClientImageActivity();
            }
        }
    }
}
