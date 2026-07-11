using Jellyfin.Plugin.JellyTag.Configuration;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.JellyTag.Services;

/// <summary>
/// Interface for quality detection service.
/// </summary>
public interface IQualityDetectionService
{
    /// <summary>
    /// Gets the video quality for an item by its ID.
    /// </summary>
    /// <param name="itemId">The item ID.</param>
    /// <returns>The detected video quality.</returns>
    VideoQuality GetQuality(Guid itemId);

    /// <summary>
    /// Gets the video quality from a base item.
    /// </summary>
    /// <param name="item">The base item.</param>
    /// <returns>The detected video quality.</returns>
    VideoQuality GetQualityFromItem(BaseItem item);

    /// <summary>
    /// Detects all applicable badges for an item (resolution, HDR, audio, etc.).
    /// </summary>
    /// <param name="item">The base item.</param>
    /// <returns>A list of detected badges.</returns>
    List<BadgeInfo> DetectAllBadges(BaseItem item, ImageTypeConfig? imageConfig = null);

    /// <summary>
    /// Clears the in-memory badge detection cache.
    /// </summary>
    void ClearBadgeCache();

    /// <summary>
    /// Rebuilds the cached badge status index.
    /// </summary>
    /// <param name="progress">Optional progress callback from 0 to 1.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the index has been rebuilt.</returns>
    Task RefreshBadgeStatusIndexAsync(Action<double>? progress, CancellationToken cancellationToken);
}
