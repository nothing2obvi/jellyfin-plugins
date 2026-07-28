using Jellyfin.Plugin.JellyTag.Configuration;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.JellyTag.Services;

/// <summary>
/// Resolves the final visible badge state for a JellyTag-Plus image target.
/// </summary>
public interface IBadgeVisibilityService
{
    /// <summary>
    /// Gets the image configuration that applies to this Jellyfin image request.
    /// </summary>
    ImageTypeConfig? GetImageTypeConfig(PluginConfiguration config, string imageType, BaseItem item);

    /// <summary>
    /// Gets the JellyTag-Plus target key for this image request.
    /// </summary>
    string GetImageTargetKey(string imageType, BaseItem item);

    /// <summary>
    /// Gets the currently visible badge state, calculating it if necessary.
    /// </summary>
    VisibleBadgeState GetVisibleBadgeState(BaseItem item, string imageType, string imageVersion, ImageTypeConfig imageConfig, PluginConfiguration config, IReadOnlyList<Folder> collectionFolders, string targetConfigFingerprint);

    /// <summary>
    /// Gets the current visible badge state only when it is already present in the durable index.
    /// </summary>
    VisibleBadgeState? TryGetIndexedVisibleBadgeState(BaseItem item, string imageType, string imageVersion, PluginConfiguration config, IReadOnlyList<Folder> collectionFolders, string targetConfigFingerprint);

    /// <summary>
    /// Rebuilds raw badge detection and final visible badge state indexes.
    /// </summary>
    Task RefreshBadgeStatusIndexAsync(Action<double>? progress, Func<BaseItem, string, VisibleBadgeState, bool, CancellationToken, Task>? changedCallback, CancellationToken cancellationToken);
}

/// <summary>
/// The visible badges and their stable state fingerprint for an image target.
/// </summary>
public sealed record VisibleBadgeState(List<BadgeInfo> Badges, string BadgeState, string BadgeKey, bool HasVisibleBadges);
