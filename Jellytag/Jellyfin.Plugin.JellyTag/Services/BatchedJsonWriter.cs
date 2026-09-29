using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTag.Services;

/// <summary>Coalesces index changes without doing serialization or disk I/O on request threads.</summary>
internal sealed class BatchedJsonWriter<T> : IDisposable
{
    private readonly Func<string?> _getPath;
    private readonly Func<T> _snapshot;
    private readonly ILogger _logger;
    private readonly Timer _timer;
    private int _dirty;

    // Clear operations use the same lock, before taking the index lock, so an old
    // snapshot cannot be published after a clear. Request updates never take this lock.
    internal object PersistenceLock { get; } = new();

    internal BatchedJsonWriter(Func<string?> getPath, Func<T> snapshot, ILogger logger)
    {
        _getPath = getPath;
        _snapshot = snapshot;
        _logger = logger;
        _timer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    internal void MarkDirty() => Interlocked.Exchange(ref _dirty, 1);

    // Call only while holding PersistenceLock and the owning index lock.
    internal void ResetPending() => Interlocked.Exchange(ref _dirty, 0);

    internal void Flush()
    {
        lock (PersistenceLock)
        {
            if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
            string? tempPath = null;
            try
            {
                var path = _getPath() ?? throw new InvalidOperationException("Index storage path is unavailable.");
                var snapshot = _snapshot();
                // Snapshot copies the dictionaries under their lock. Entries are replaced,
                // never mutated, so serialization can safely run after releasing that lock.
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(snapshot));
                File.Move(tempPath, path, overwrite: true);
            }
            catch (Exception ex)
            {
                MarkDirty();
                _logger.LogWarning(ex, "Failed to persist JellyTag index; will retry");
            }
            finally
            {
                if (tempPath != null)
                {
                    try { File.Delete(tempPath); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Failed to remove temporary index file"); }
                }
            }
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        Flush();
    }
}
