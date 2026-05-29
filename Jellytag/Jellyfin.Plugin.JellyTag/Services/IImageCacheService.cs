namespace Jellyfin.Plugin.JellyTag.Services;

/// <summary>
/// Interface for image cache service.
/// </summary>
public interface IImageCacheService
{
    /// <summary>
    /// Creates a stable request-level cache key for a specific image variant.
    /// </summary>
    /// <param name="itemId">The item ID.</param>
    /// <param name="imageType">The Jellyfin image type.</param>
    /// <param name="imageVersion">The source image version.</param>
    /// <param name="query">The cache-relevant request query string.</param>
    /// <param name="itemModifiedTicks">The item modified ticks.</param>
    /// <returns>A request-level cache key.</returns>
    string CreateRequestCacheKey(Guid itemId, string imageType, string imageVersion, string query, long itemModifiedTicks);

    /// <summary>
    /// Gets a cached image file for a previously learned request-level cache key.
    /// </summary>
    /// <param name="itemId">The item ID.</param>
    /// <param name="requestCacheKey">The request-level cache key.</param>
    /// <returns>The cached image file, or null if not learned, missing, or expired.</returns>
    Task<CachedImageFile?> GetCachedImageFileForRequestAsync(Guid itemId, string requestCacheKey);

    /// <summary>
    /// Learns the final cache state for a request-level cache key.
    /// </summary>
    /// <param name="requestCacheKey">The request-level cache key.</param>
    /// <param name="itemId">The item ID.</param>
    /// <param name="badgeKey">The composite badge key.</param>
    /// <param name="imageTag">The image tag/etag.</param>
    /// <param name="badgeState">The badge state fingerprint.</param>
    void SetRequestCacheEntry(string requestCacheKey, Guid itemId, string badgeKey, string imageTag, string badgeState);

    /// <summary>
    /// Gets a cached image file if available and not expired.
    /// </summary>
    /// <param name="itemId">The item ID.</param>
    /// <param name="badgeKey">The composite badge key (e.g. "4k_hdr10_atmos").</param>
    /// <param name="imageTag">The image tag/etag for cache invalidation.</param>
    /// <param name="badgeState">The badge state fingerprint.</param>
    /// <returns>The cached image file, or null if not cached.</returns>
    Task<CachedImageFile?> GetCachedImageFileAsync(Guid itemId, string badgeKey, string imageTag, string badgeState);

    /// <summary>
    /// Gets a cached image if available and not expired.
    /// </summary>
    /// <param name="itemId">The item ID.</param>
    /// <param name="badgeKey">The composite badge key (e.g. "4k_hdr10_atmos").</param>
    /// <param name="imageTag">The image tag/etag for cache invalidation.</param>
    /// <returns>The cached image stream, or null if not cached.</returns>
    Task<Stream?> GetCachedImageAsync(Guid itemId, string badgeKey, string imageTag);

    /// <summary>
    /// Caches an image.
    /// </summary>
    /// <param name="itemId">The item ID.</param>
    /// <param name="badgeKey">The composite badge key.</param>
    /// <param name="imageTag">The image tag/etag.</param>
    /// <param name="imageStream">The image stream to cache.</param>
    /// <returns>True when the cache file was written successfully; otherwise false.</returns>
    Task<bool> CacheImageAsync(Guid itemId, string badgeKey, string imageTag, string badgeState, Stream imageStream);

    /// <summary>
    /// Clears all cached images.
    /// </summary>
    void ClearCache();

    /// <summary>
    /// Invalidates cache for a specific item.
    /// </summary>
    /// <param name="itemId">The item ID.</param>
    void InvalidateCache(Guid itemId);

    /// <summary>
    /// Removes stale cache index entries for files that no longer exist or have expired.
    /// </summary>
    void PruneCacheIndex();

    /// <summary>
    /// Gets the cache directory path.
    /// </summary>
    /// <returns>The absolute path to the cache directory.</returns>
    string GetCacheDirectory();

    /// <summary>
    /// Gets cache statistics.
    /// </summary>
    /// <returns>A tuple of (FileCount, TotalSizeBytes, OldestEntry, NewestEntry).</returns>
    (int FileCount, long TotalSizeBytes, DateTime? OldestEntry, DateTime? NewestEntry) GetCacheStats();
}

/// <summary>
/// Metadata for a cached JellyTag-Plus image file.
/// </summary>
public sealed record CachedImageFile(string Path, string ContentType, long Length, string BadgeState);
