using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTag.Services;

/// <summary>
/// Service for caching modified images.
/// </summary>
public class ImageCacheService : IImageCacheService
{
    private readonly ILogger<ImageCacheService> _logger;
    private readonly string _cachePath;
    private readonly object _lock = new();
    private CacheIndex? _cacheIndex;
    private bool _cacheIndexLoaded;

    private const string CacheIndexFileName = "cache-index.json";
    private const string CacheMetadataFolderName = "metadata";

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageCacheService"/> class.
    /// </summary>
    public ImageCacheService(ILogger<ImageCacheService> logger)
    {
        _logger = logger;
        _cachePath = Plugin.Instance?.CacheFolderPath ?? Path.Combine(Path.GetTempPath(), "JellyTag", "cache");
        EnsureCacheDirectoryExists();
    }

    /// <inheritdoc />
    public string CreateRequestCacheKey(Guid itemId, string imageType, string imageVersion, string query, long itemModifiedTicks)
    {
        var config = Plugin.Instance?.Configuration;
        var configFingerprint = config != null ? ComputeConfigFingerprint(config) : string.Empty;
        var input = $"{itemId:N}_{imageType}_{imageVersion}_{itemModifiedTicks}_{query}_{configFingerprint}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return $"{itemId:N}_{Convert.ToHexString(hashBytes)[..16]}";
    }

    /// <inheritdoc />
    public Task<CachedImageFile?> GetCachedImageFileForRequestAsync(Guid itemId, string requestCacheKey)
    {
        RequestCacheIndexEntry? requestEntry;
        lock (_lock)
        {
            var index = GetCacheIndexLocked();
            if (!index.RequestEntries.TryGetValue(requestCacheKey, out requestEntry))
            {
                return Task.FromResult<CachedImageFile?>(null);
            }
        }

        var cachedFile = TryGetCachedFile(itemId, requestEntry.BadgeKey, requestEntry.ImageTag, requestEntry.BadgeState, allowExpiredValidation: false);
        if (cachedFile == null)
        {
            RemoveRequestCacheEntry(requestCacheKey);
        }

        return Task.FromResult(cachedFile);
    }

    /// <inheritdoc />
    public void SetRequestCacheEntry(string requestCacheKey, Guid itemId, string badgeKey, string imageTag, string badgeState)
    {
        var finalCacheKey = GenerateCacheKey(itemId, badgeKey, imageTag);
        lock (_lock)
        {
            var index = GetCacheIndexLocked();
            index.RequestEntries[requestCacheKey] = new RequestCacheIndexEntry
            {
                ItemId = itemId.ToString("N"),
                BadgeKey = badgeKey,
                ImageTag = imageTag,
                BadgeState = badgeState,
                FinalCacheKey = finalCacheKey,
                UpdatedUtcTicks = DateTime.UtcNow.Ticks
            };
            SaveCacheIndexLocked();
        }
    }

    /// <inheritdoc />
    public Task<CachedImageFile?> GetCachedImageFileAsync(Guid itemId, string badgeKey, string imageTag, string badgeState)
    {
        return Task.FromResult(TryGetCachedFile(itemId, badgeKey, imageTag, badgeState, allowExpiredValidation: true));
    }

    /// <inheritdoc />
    public Task<Stream?> GetCachedImageAsync(Guid itemId, string badgeKey, string imageTag)
    {
        var cacheKey = GenerateCacheKey(itemId, badgeKey, imageTag);
        var indexedPath = GetIndexedCachePath(cacheKey);
        if (!string.IsNullOrWhiteSpace(indexedPath))
        {
            var indexedStream = TryOpenCachedFile(itemId, cacheKey, indexedPath, updateIndex: false);
            if (indexedStream != null)
            {
                return Task.FromResult<Stream?>(indexedStream);
            }
        }

        var cacheFilePath = GetCachePath(cacheKey);
        var stream = TryOpenCachedFile(itemId, cacheKey, cacheFilePath, updateIndex: true);
        return Task.FromResult(stream);
    }

    private CachedImageFile? TryGetCachedFile(Guid itemId, string badgeKey, string imageTag, string badgeState, bool allowExpiredValidation)
    {
        var cacheKey = GenerateCacheKey(itemId, badgeKey, imageTag);
        var indexedPath = GetIndexedCachePath(cacheKey);
        if (!string.IsNullOrWhiteSpace(indexedPath))
        {
            var indexedFile = TryGetCachedFile(itemId, cacheKey, indexedPath, badgeState, false, allowExpiredValidation);
            if (indexedFile != null)
            {
                return indexedFile;
            }
        }

        return TryGetCachedFile(itemId, cacheKey, GetCachePath(cacheKey), badgeState, true, allowExpiredValidation);
    }

    private CachedImageFile? TryGetCachedFile(Guid itemId, string cacheKey, string cacheFilePath, string badgeState, bool updateIndex, bool allowExpiredValidation)
    {
        if (!File.Exists(cacheFilePath))
        {
            RemoveCacheIndexEntry(cacheKey);
            return null;
        }

        var fileInfo = new FileInfo(cacheFilePath);
        if (IsExpired(fileInfo))
        {
            if (!allowExpiredValidation && ShouldValidateExpiredCache())
            {
                return null;
            }

            if (allowExpiredValidation && ShouldValidateExpiredCache() && TryValidateExpiredCacheFile(cacheKey, cacheFilePath, badgeState, fileInfo))
            {
                fileInfo.Refresh();
                if (updateIndex)
                {
                    SetCacheIndexEntry(cacheKey, cacheFilePath);
                }

                _logger.LogDebug("Validated expired JellyTag-Plus cache file for item {ItemId}", itemId);
                return new CachedImageFile(cacheFilePath, GetContentType(), fileInfo.Length, badgeState);
            }

            _logger.LogDebug("Cache expired for item {ItemId}", itemId);
            TryDeleteExpiredCacheFile(cacheKey, cacheFilePath);
            return null;
        }

        if (updateIndex)
        {
            SetCacheIndexEntry(cacheKey, cacheFilePath);
        }

        TryWriteCacheMetadata(cacheKey, cacheFilePath, badgeState, fileInfo);
        _logger.LogDebug("Cache file hit for item {ItemId}", itemId);
        return new CachedImageFile(cacheFilePath, GetContentType(), fileInfo.Length, badgeState);
    }

    private Stream? TryOpenCachedFile(Guid itemId, string cacheKey, string cacheFilePath, bool updateIndex)
    {
        if (!File.Exists(cacheFilePath))
        {
            RemoveCacheIndexEntry(cacheKey);
            return null;
        }

        var fileInfo = new FileInfo(cacheFilePath);

        if (IsExpired(fileInfo))
        {
            _logger.LogDebug("Cache expired for item {ItemId}", itemId);
            TryDeleteExpiredCacheFile(cacheKey, cacheFilePath);
            return null;
        }

        if (updateIndex)
        {
            SetCacheIndexEntry(cacheKey, cacheFilePath);
        }

        _logger.LogDebug("Cache hit for item {ItemId}", itemId);
        return new FileStream(cacheFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
    }

    /// <inheritdoc />
    public async Task<bool> CacheImageAsync(Guid itemId, string badgeKey, string imageTag, string badgeState, Stream imageStream)
    {
        var cacheKey = GenerateCacheKey(itemId, badgeKey, imageTag);
        var cachePath = GetCachePath(cacheKey);
        var tempPath = cachePath + ".tmp";

        try
        {
            EnsureCacheDirectoryExists();
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

            using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
            {
                await imageStream.CopyToAsync(fileStream).ConfigureAwait(false);
            }

            File.Move(tempPath, cachePath, overwrite: true);
            SetCacheIndexEntry(cacheKey, cachePath);
            TryWriteCacheMetadata(cacheKey, cachePath, badgeState, new FileInfo(cachePath));

            _logger.LogDebug("Cached image for item {ItemId} at {Path}", itemId, cachePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cache image for item {ItemId}", itemId);

            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }

            return false;
        }
    }

    /// <inheritdoc />
    public string GetCacheDirectory() => _cachePath;

    /// <inheritdoc />
    public void ClearCache()
    {
        lock (_lock)
        {
            try
            {
                if (Directory.Exists(_cachePath))
                {
                    var jpgFiles = Directory.GetFiles(_cachePath, "*.jpg", SearchOption.AllDirectories);
                    var webpFiles = Directory.GetFiles(_cachePath, "*.webp", SearchOption.AllDirectories);
                    var warmerStateFiles = Directory.GetFiles(_cachePath, "cache-warmer-state.json*");
                    var indexFiles = Directory.GetFiles(_cachePath, CacheIndexFileName + "*");
                    var metadataRoot = GetCacheMetadataRoot();
                    var metadataFiles = Directory.Exists(metadataRoot)
                        ? Directory.GetFiles(metadataRoot, "*.json", SearchOption.AllDirectories)
                        : Array.Empty<string>();
                    var files = jpgFiles.Concat(webpFiles).Concat(warmerStateFiles).Concat(indexFiles).Concat(metadataFiles).ToArray();
                    foreach (var file in files)
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete cache file: {Path}", file);
                        }
                    }

                    foreach (var directory in Directory.GetDirectories(_cachePath, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                    {
                        try
                        {
                            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                            {
                                Directory.Delete(directory);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete empty cache directory: {Path}", directory);
                        }
                    }

                    _cacheIndex = new CacheIndex();
                    _cacheIndexLoaded = true;
                    _logger.LogInformation("Cleared {Count} cached images", files.Length);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear cache");
            }
        }
    }

    /// <inheritdoc />
    public void InvalidateCache(Guid itemId)
    {
        try
        {
            var jpgPattern = $"{itemId}_*.jpg";
            var webpPattern = $"{itemId}_*.webp";
            var files = Directory.GetFiles(_cachePath, jpgPattern, SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(_cachePath, webpPattern, SearchOption.AllDirectories))
                .ToArray();
            foreach (var file in files)
            {
                try
                {
                    File.Delete(file);
                    _logger.LogDebug("Invalidated cache for item {ItemId}: {File}", itemId, file);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete cache file: {Path}", file);
                }
            }

            RemoveCacheIndexEntriesForItem(itemId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate cache for item {ItemId}", itemId);
        }
    }

    /// <inheritdoc />
    public void PruneCacheIndex()
    {
        lock (_lock)
        {
            var index = GetCacheIndexLocked();
            var removed = 0;
            foreach (var entry in index.Entries.ToArray())
            {
                var absolutePath = ToAbsolutePath(entry.Value.RelativePath);
                if (!File.Exists(absolutePath) || (!ShouldValidateExpiredCache() && IsExpired(new FileInfo(absolutePath))))
                {
                    index.Entries.Remove(entry.Key);
                    TryDeleteCacheMetadata(entry.Key);
                    removed++;
                }
            }

            if (removed > 0)
            {
                PruneRequestEntriesLocked(index);
                SaveCacheIndexLocked();
                _logger.LogDebug("Pruned {Count} stale JellyTag-Plus cache index entries", removed);
            }
        }
    }

    /// <inheritdoc />
    public (int FileCount, long TotalSizeBytes, DateTime? OldestEntry, DateTime? NewestEntry) GetCacheStats()
    {
        try
        {
            if (!Directory.Exists(_cachePath))
            {
                return (0, 0, null, null);
            }

            var jpgFiles = Directory.GetFiles(_cachePath, "*.jpg", SearchOption.AllDirectories);
            var webpFiles = Directory.GetFiles(_cachePath, "*.webp", SearchOption.AllDirectories);
            var allFiles = jpgFiles.Concat(webpFiles).Select(f => new FileInfo(f)).ToArray();

            if (allFiles.Length == 0)
            {
                return (0, 0, null, null);
            }

            var totalSize = allFiles.Sum(f => f.Length);
            var oldest = allFiles.Min(f => f.LastWriteTimeUtc);
            var newest = allFiles.Max(f => f.LastWriteTimeUtc);

            return (allFiles.Length, totalSize, oldest, newest);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get cache stats");
            return (0, 0, null, null);
        }
    }

    private string GenerateCacheKey(Guid itemId, string badgeKey, string imageTag)
    {
        var config = Plugin.Instance?.Configuration;
        var configFingerprint = config != null ? ComputeConfigFingerprint(config) : string.Empty;
        var input = $"{itemId}_{badgeKey}_{imageTag}_{configFingerprint}";

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var hash = Convert.ToHexString(hashBytes)[..16];

        return $"{itemId}_{hash}";
    }

    private static string ComputeConfigFingerprint(Configuration.PluginConfiguration config)
    {
        var sb = new StringBuilder(256);
        sb.Append(config.Enabled).Append('|');
        sb.Append((int)config.OutputFormat).Append(config.JpegQuality).Append(config.WebPQuality).Append('|');
        sb.Append(config.ThumbnailSameAsPoster).Append('|');
        sb.Append(string.Join(",", (config.ExcludedLibraryIds ?? new List<string>()).OrderBy(id => id, StringComparer.OrdinalIgnoreCase))).Append('|');
        if (config.LibraryBadgeOptions != null)
        {
            foreach (var option in config.LibraryBadgeOptions.OrderBy(o => o.LibraryId, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append(option.LibraryId).Append(':')
                    .Append(option.Resolution).Append(option.Hdr).Append(option.Codec)
                    .Append(option.Audio).Append(option.Language).Append(option.Collections).Append(',');
            }
        }
        sb.Append('|');
        AppendImageTypeFingerprint(sb, config.PosterConfig);
        AppendImageTypeFingerprint(sb, config.ThumbnailConfig);
        if (config.CustomBadgeTexts != null)
        {
            foreach (var cbt in config.CustomBadgeTexts.OrderBy(cbt => cbt.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append(cbt.Key).Append('=').Append(cbt.Text).Append(',');
            }
        }

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hashBytes)[..16];
    }

    private static void AppendImageTypeFingerprint(StringBuilder sb, Configuration.ImageTypeConfig c)
    {
        sb.Append(c.Enabled).Append('|');
        AppendPanelFingerprint(sb, c.ResolutionPanel);
        AppendPanelFingerprint(sb, c.HdrPanel);
        AppendPanelFingerprint(sb, c.CodecPanel);
        AppendPanelFingerprint(sb, c.AudioPanel);
        AppendPanelFingerprint(sb, c.LanguagePanel);
        AppendPanelFingerprint(sb, c.CollectionPanel);
        sb.Append(c.CollectionRegex ?? "n").Append(c.CollectionBadgeText ?? "n").Append('|');
        if (c.CollectionRules != null)
        {
            foreach (var rule in c.CollectionRules)
            {
                sb.Append(rule.Key).Append('=').Append(rule.Regex).Append('=').Append(rule.Label)
                    .Append('=').Append(rule.ShowOnPosters).Append(rule.ShowOnSeasonPosters).Append(rule.ShowOnSeriesThumbnails).Append(rule.ShowOnEpisodeThumbnails).Append(',');
            }
        }
        sb.Append('|');
        sb.Append(c.ShowVostIndicator).Append(c.VostBgColor ?? "n").Append(c.VostTextColor ?? "n");
        sb.Append(c.VostBgOpacity).Append(c.VostCornerRadius).Append('|');
    }

    private static void AppendPanelFingerprint(StringBuilder sb, Configuration.BadgePanelSettings p)
    {
        sb.Append(p.Enabled).Append((int)p.Position).Append((int)p.ShowMode);
        sb.Append((int)p.Layout).Append(p.GapPercent).Append(p.SizePercent).Append(p.MarginPercent);
        sb.Append((int)p.Style).Append(p.Order);
        sb.Append(p.TextBgColor).Append(p.TextBgOpacity).Append(p.TextColor).Append(p.TextCornerRadius);
        sb.Append(string.Join(",", (p.EnabledBadges ?? new List<string>()).OrderBy(badge => badge, StringComparer.OrdinalIgnoreCase)));
        if (p.BadgeTypeOverrides != null)
        {
            foreach (var o in p.BadgeTypeOverrides.OrderBy(o => o.BadgeKey, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append(o.BadgeKey).Append(o.BgColor ?? "n").Append(o.BgOpacity).Append(o.TextColor ?? "n").Append(o.CornerRadius);
            }
        }
        sb.Append('|');
    }

    private string GetCachePath(string cacheKey)
    {
        var config = Plugin.Instance?.Configuration;
        var ext = config?.OutputFormat == Configuration.OutputImageFormat.WebP ? ".webp" : ".jpg";
        return Path.Combine(_cachePath, GetCachePrefix(cacheKey), $"{cacheKey}{ext}");
    }

    private static string GetContentType()
    {
        var config = Plugin.Instance?.Configuration;
        return config?.OutputFormat == Configuration.OutputImageFormat.WebP ? "image/webp" : "image/jpeg";
    }

    private static string GetCachePrefix(string cacheKey)
    {
        var hashStart = cacheKey.LastIndexOf('_') + 1;
        var hash = hashStart > 0 && hashStart < cacheKey.Length ? cacheKey[hashStart..] : cacheKey;
        return hash.Length >= 2 ? hash[..2].ToLowerInvariant() : "00";
    }

    private bool IsExpired(FileInfo fileInfo)
    {
        var config = Plugin.Instance?.Configuration;
        var cacheHours = config?.CacheDurationHours ?? 168;
        return cacheHours > 0 && (DateTime.UtcNow - fileInfo.LastWriteTimeUtc).TotalHours > cacheHours;
    }

    private static bool ShouldValidateExpiredCache()
    {
        var config = Plugin.Instance?.Configuration;
        return config?.ValidateExpiredCacheBeforeRerender == true && (config.CacheDurationHours > 0);
    }

    private bool TryValidateExpiredCacheFile(string cacheKey, string cacheFilePath, string badgeState, FileInfo fileInfo)
    {
        var metadata = TryReadCacheMetadata(cacheKey);
        if (metadata != null &&
            (!string.Equals(metadata.CacheKey, cacheKey, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(metadata.BadgeState, badgeState, StringComparison.Ordinal)))
        {
            return false;
        }

        try
        {
            var now = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(cacheFilePath, now);
            TryWriteCacheMetadata(cacheKey, cacheFilePath, badgeState, fileInfo, now);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to validate expired JellyTag-Plus cache file: {Path}", cacheFilePath);
            return false;
        }
    }

    private CacheMetadataEntry? TryReadCacheMetadata(string cacheKey)
    {
        var path = GetCacheMetadataPath(cacheKey);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CacheMetadataEntry>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read JellyTag-Plus cache metadata: {Path}", path);
            return null;
        }
    }

    private void TryWriteCacheMetadata(string cacheKey, string cacheFilePath, string badgeState, FileInfo fileInfo, DateTime? validatedUtc = null)
    {
        if (!ShouldValidateExpiredCache())
        {
            return;
        }

        try
        {
            var path = GetCacheMetadataPath(cacheKey);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var now = DateTime.UtcNow;
            var metadata = new CacheMetadataEntry
            {
                CacheKey = cacheKey,
                BadgeState = badgeState,
                RelativePath = ToRelativePath(cacheFilePath),
                Length = fileInfo.Exists ? fileInfo.Length : 0,
                CreatedUtcTicks = fileInfo.Exists ? fileInfo.CreationTimeUtc.Ticks : now.Ticks,
                LastValidatedUtcTicks = (validatedUtc ?? now).Ticks
            };
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(metadata));
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to write JellyTag-Plus cache metadata for {CacheKey}", cacheKey);
        }
    }

    private void TryDeleteCacheMetadata(string cacheKey)
    {
        try
        {
            var path = GetCacheMetadataPath(cacheKey);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete JellyTag-Plus cache metadata for {CacheKey}", cacheKey);
        }
    }

    private void TryDeleteExpiredCacheFile(string cacheKey, string cacheFilePath)
    {
        try
        {
            File.Delete(cacheFilePath);
            RemoveCacheIndexEntry(cacheKey);
            TryDeleteCacheMetadata(cacheKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete expired cache file: {Path}", cacheFilePath);
        }
    }

    private string? GetIndexedCachePath(string cacheKey)
    {
        lock (_lock)
        {
            var index = GetCacheIndexLocked();
            return index.Entries.TryGetValue(cacheKey, out var entry) ? ToAbsolutePath(entry.RelativePath) : null;
        }
    }

    private void SetCacheIndexEntry(string cacheKey, string cachePath)
    {
        lock (_lock)
        {
            var index = GetCacheIndexLocked();
            index.Entries[cacheKey] = new CacheIndexEntry
            {
                RelativePath = ToRelativePath(cachePath),
                UpdatedUtcTicks = DateTime.UtcNow.Ticks
            };
            SaveCacheIndexLocked();
        }
    }

    private void RemoveCacheIndexEntry(string cacheKey)
    {
        lock (_lock)
        {
            var index = GetCacheIndexLocked();
            var removed = index.Entries.Remove(cacheKey);
            removed |= RemoveRequestEntriesForFinalCacheKeyLocked(index, cacheKey);
            if (removed)
            {
                TryDeleteCacheMetadata(cacheKey);
                SaveCacheIndexLocked();
            }
        }
    }

    private void RemoveRequestCacheEntry(string requestCacheKey)
    {
        lock (_lock)
        {
            var index = GetCacheIndexLocked();
            if (index.RequestEntries.Remove(requestCacheKey))
            {
                SaveCacheIndexLocked();
            }
        }
    }

    private void RemoveCacheIndexEntriesForItem(Guid itemId)
    {
        lock (_lock)
        {
            var index = GetCacheIndexLocked();
            var normalizedPrefix = itemId.ToString("N") + "_";
            var defaultPrefix = itemId + "_";
            var itemIdString = itemId.ToString("N");
            var removed = false;
            foreach (var key in index.Entries.Keys.Where(k =>
                    k.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase) ||
                    k.StartsWith(defaultPrefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                removed |= index.Entries.Remove(key);
                TryDeleteCacheMetadata(key);
            }

            foreach (var key in index.RequestEntries.Where(kvp => string.Equals(kvp.Value.ItemId, itemIdString, StringComparison.OrdinalIgnoreCase)).Select(kvp => kvp.Key).ToArray())
            {
                removed |= index.RequestEntries.Remove(key);
            }

            if (removed)
            {
                SaveCacheIndexLocked();
            }
        }
    }

    private CacheIndex GetCacheIndexLocked()
    {
        if (_cacheIndexLoaded)
        {
            return _cacheIndex ??= new CacheIndex();
        }

        _cacheIndexLoaded = true;
        var path = GetCacheIndexPath();
        if (!File.Exists(path))
        {
            _cacheIndex = new CacheIndex();
            return _cacheIndex;
        }

        try
        {
            _cacheIndex = JsonSerializer.Deserialize<CacheIndex>(File.ReadAllText(path)) ?? new CacheIndex();
            _cacheIndex.Entries ??= new Dictionary<string, CacheIndexEntry>(StringComparer.OrdinalIgnoreCase);
            _cacheIndex.RequestEntries ??= new Dictionary<string, RequestCacheIndexEntry>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load JellyTag-Plus cache index; rebuilding as cache entries are used");
            _cacheIndex = new CacheIndex();
        }

        return _cacheIndex;
    }

    private static bool RemoveRequestEntriesForFinalCacheKeyLocked(CacheIndex index, string finalCacheKey)
    {
        var removed = false;
        foreach (var key in index.RequestEntries
            .Where(kvp => string.Equals(kvp.Value.FinalCacheKey, finalCacheKey, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .ToArray())
        {
            removed |= index.RequestEntries.Remove(key);
        }

        return removed;
    }

    private void PruneRequestEntriesLocked(CacheIndex index)
    {
        foreach (var key in index.RequestEntries
            .Where(kvp => !index.Entries.ContainsKey(kvp.Value.FinalCacheKey))
            .Select(kvp => kvp.Key)
            .ToArray())
        {
            index.RequestEntries.Remove(key);
        }
    }

    private void SaveCacheIndexLocked()
    {
        EnsureCacheDirectoryExists();
        var path = GetCacheIndexPath();
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(GetCacheIndexLocked()));
        File.Move(tempPath, path, overwrite: true);
    }

    private string GetCacheIndexPath() => Path.Combine(_cachePath, CacheIndexFileName);

    private string GetCacheMetadataRoot() => Path.Combine(_cachePath, CacheMetadataFolderName);

    private string GetCacheMetadataPath(string cacheKey) => Path.Combine(GetCacheMetadataRoot(), GetCachePrefix(cacheKey), $"{cacheKey}.json");

    private string ToRelativePath(string path) => Path.GetRelativePath(_cachePath, path);

    private string ToAbsolutePath(string relativePath) => Path.GetFullPath(Path.Combine(_cachePath, relativePath));

    private void EnsureCacheDirectoryExists()
    {
        if (!Directory.Exists(_cachePath))
        {
            Directory.CreateDirectory(_cachePath);
        }
    }

    private sealed class CacheIndex
    {
        public CacheIndex()
        {
        }

        public int Version { get; set; } = 1;

        public Dictionary<string, CacheIndexEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, RequestCacheIndexEntry> RequestEntries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class CacheIndexEntry
    {
        public CacheIndexEntry()
        {
        }

        public string RelativePath { get; set; } = string.Empty;

        public long UpdatedUtcTicks { get; set; }
    }

    private sealed class RequestCacheIndexEntry
    {
        public RequestCacheIndexEntry()
        {
        }

        public string ItemId { get; set; } = string.Empty;

        public string BadgeKey { get; set; } = string.Empty;

        public string ImageTag { get; set; } = string.Empty;

        public string BadgeState { get; set; } = string.Empty;

        public string FinalCacheKey { get; set; } = string.Empty;

        public long UpdatedUtcTicks { get; set; }
    }

    private sealed class CacheMetadataEntry
    {
        public CacheMetadataEntry()
        {
        }

        public int Version { get; set; } = 1;

        public string CacheKey { get; set; } = string.Empty;

        public string BadgeState { get; set; } = string.Empty;

        public string RelativePath { get; set; } = string.Empty;

        public long Length { get; set; }

        public long CreatedUtcTicks { get; set; }

        public long LastValidatedUtcTicks { get; set; }
    }
}
