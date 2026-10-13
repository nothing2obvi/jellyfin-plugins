using Jellyfin.Plugin.ArtistPruner.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.ArtistPruner.Tasks;

public sealed class PruneArtistsTask(IArtistPruneService service) : IScheduledTask
{
    public string Name => "Prune Music Artists";
    public string Key => "ArtistPruner_PruneMusicArtists";
    public string Description => "Preview or correct supported music album-artist associations with recovery backups.";
    public string Category => "Artist Pruner";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        await service.PruneAsync(progress, cancellationToken).ConfigureAwait(false);
}
