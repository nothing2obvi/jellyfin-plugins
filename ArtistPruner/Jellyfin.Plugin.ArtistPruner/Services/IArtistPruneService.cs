using Jellyfin.Plugin.ArtistPruner.Models;

namespace Jellyfin.Plugin.ArtistPruner.Services;

public interface IArtistPruneService
{
    Task<PruneResult> PruneAsync(IProgress<double>? progress, CancellationToken cancellationToken);
}
