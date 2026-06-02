using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyTag.Configuration;
using Jellyfin.Plugin.JellyTag.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTag.Middleware;

/// <summary>
/// Middleware that intercepts Jellyfin image requests and adds quality badge overlays.
/// </summary>
public partial class ImageOverlayMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ImageOverlayMiddleware> _logger;
    private const string ForceRefreshStateFileName = "force-refresh-state.json";
    private const string WarmupResultHeader = "X-JellyTag-Warmup-Result";
    private const string WarmupResultCacheHit = "cache-hit";
    private const string WarmupResultCacheWritten = "cache-written";
    private const string WarmupResultNoVisibleBadges = "no-visible-badges";
    private const string WarmupResultCacheWriteFailed = "cache-write-failed";
    private const string WarmupResultPassThrough = "pass-through";
    private const string WarmupResultOverlayError = "overlay-error";
    private static readonly ConcurrentDictionary<string, string> ForceRefreshStates = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ForceRefreshLocks = new();
    private static readonly ConcurrentDictionary<string, RenderLockState> RenderLocks = new();
    private static readonly object RenderLocksLock = new();
    private static readonly object ForceRefreshStateFileLock = new();
    private static bool ForceRefreshStateLoaded;
    private static readonly string EmptyBadgeState = GetBadgeStateFingerprint(Array.Empty<BadgeInfo>());
    private static readonly byte[] StockRefreshImage = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=");

    [GeneratedRegex(@"/Items/([0-9a-f]{32}|[0-9a-f-]{36})/Images/(Primary|Thumb)(/\d+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ImagePathRegex();

    public ImageOverlayMiddleware(RequestDelegate next, ILogger<ImageOverlayMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }


    public static void ResetForceRefreshState()
    {
        lock (ForceRefreshStateFileLock)
        {
            ForceRefreshStates.Clear();
            ForceRefreshStateLoaded = true;
            var path = GetForceRefreshStatePath();
            if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
    }

    public async Task InvokeAsync(
        HttpContext context,
        IQualityDetectionService qualityService,
        IImageOverlayService overlayService,
        IImageCacheService cacheService,
        IImageTrafficCoordinator trafficCoordinator,
        ILearnedClientProfileService learnedClientProfileService,
        MediaBrowser.Controller.Library.ILibraryManager libraryManager,
        IProviderManager providerManager,
        IAuthorizationContext authorizationContext,
        ISessionManager sessionManager)
    {
        var path = context.Request.Path.Value;
        if (path == null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var match = ImagePathRegex().Match(path);
        if (!match.Success)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (IsBypassRequest(context.Request.Query))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.Enabled)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var itemIdStr = match.Groups[1].Value;
        var imageType = match.Groups[2].Value;

        if (!IsWarmupRequest(context.Request.Query))
        {
            trafficCoordinator.NotifyClientImageRequest();
        }

        if (!Guid.TryParse(itemIdStr, out var itemId))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var item = libraryManager.GetItemById(itemId);
        if (item == null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (item is not (Movie or Series or Season or Episode or Video))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var collectionFolders = libraryManager.GetCollectionFolders(item).ToList();

        // Check if item's library is excluded
        if (config.ExcludedLibraryIds?.Count > 0)
        {
            if (collectionFolders.Any(f => config.ExcludedLibraryIds.Contains(f.Id.ToString("N"))))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }
        }

        var imageConfig = GetImageTypeConfig(config, imageType, item);
        if (imageConfig == null || !imageConfig.Enabled)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (!IsWarmupRequest(context.Request.Query))
        {
            var authorizationInfo = await authorizationContext.GetAuthorizationInfo(context).ConfigureAwait(false);
            var sessionInfo = await GetRequestSessionInfoAsync(context, authorizationInfo, sessionManager).ConfigureAwait(false);
            learnedClientProfileService.RecordVariant(item, imageType, context.Request.Query, context.Request.Headers, context.User, authorizationInfo, sessionInfo);
        }

        var query = GetCacheRelevantQuery(context.Request.Query);
        var imageVersion = GetImageVersion(item, imageType);
        var requestCacheKey = cacheService.CreateRequestCacheKey(itemId, imageType, imageVersion, query, item.DateModified.Ticks);
        var requestCachedFile = await cacheService.GetCachedImageFileForRequestAsync(itemId, requestCacheKey).ConfigureAwait(false);
        if (requestCachedFile != null)
        {
            await TryForceImageRefreshAsync(item, imageType, requestCachedFile.BadgeState, true, providerManager, context.RequestAborted).ConfigureAwait(false);
            SetWarmupResult(context, WarmupResultCacheHit);
            await ServeCachedImageFileAsync(context, requestCachedFile).ConfigureAwait(false);
            return;
        }

        // Detect all badges and filter by config
        var allBadges = qualityService.DetectAllBadges(item, imageConfig);
        _logger.LogDebug("DetectAllBadges for {Item}: {Count} badges found: {Badges}",
            item.Name, allBadges.Count, string.Join(", ", allBadges.Select(b => $"{b.Category}:{b.BadgeKey}")));

        var visibleBadges = allBadges
            .Where(b => overlayService.ShouldShowBadge(b, imageConfig))
            .Where(b => ShouldShowCollectionBadgeForImage(b, imageConfig, imageType, item))
            .Where(b => ShouldShowBadgeForLibrary(b, config, collectionFolders))
            .ToList();
        _logger.LogDebug("Visible badges after filter: {Count}: {Badges}",
            visibleBadges.Count, string.Join(", ", visibleBadges.Select(b => b.BadgeKey)));

        var badgeState = GetBadgeStateFingerprint(visibleBadges);
        await TryForceImageRefreshAsync(item, imageType, badgeState, visibleBadges.Count > 0, providerManager, context.RequestAborted).ConfigureAwait(false);

        if (visibleBadges.Count == 0)
        {
            SetWarmupResult(context, WarmupResultNoVisibleBadges);
            await _next(context).ConfigureAwait(false);
            return;
        }

        var badgeKey = string.Join("_", visibleBadges.Select(b => b.BadgeKey));
        _logger.LogInformation("Applying {Count} badges to {Item}: {BadgeKey}", visibleBadges.Count, item.Name, badgeKey);

        var imageTag = $"{imageVersion}_{imageType}_{query}_{badgeState}";

        var cachedFile = await cacheService.GetCachedImageFileAsync(itemId, badgeKey, imageTag, badgeState).ConfigureAwait(false);
        if (cachedFile != null)
        {
            cacheService.SetRequestCacheEntry(requestCacheKey, itemId, badgeKey, imageTag, badgeState);
            SetWarmupResult(context, WarmupResultCacheHit);
            await ServeCachedImageFileAsync(context, cachedFile).ConfigureAwait(false);
            return;
        }

        var renderKey = $"{itemId:N}:{badgeKey}:{imageTag}";
        var renderLock = RentRenderLock(renderKey);
        var renderLockAcquired = false;

        var originalBody = context.Response.Body;
        using var capturedBody = new MemoryStream();

        try
        {
            await renderLock.Semaphore.WaitAsync(context.RequestAborted).ConfigureAwait(false);
            renderLockAcquired = true;

            cachedFile = await cacheService.GetCachedImageFileAsync(itemId, badgeKey, imageTag, badgeState).ConfigureAwait(false);
            if (cachedFile != null)
            {
                cacheService.SetRequestCacheEntry(requestCacheKey, itemId, badgeKey, imageTag, badgeState);
                SetWarmupResult(context, WarmupResultCacheHit);
                await ServeCachedImageFileAsync(context, cachedFile).ConfigureAwait(false);
                return;
            }

            context.Response.Body = capturedBody;
            await _next(context).ConfigureAwait(false);

            if (context.Response.StatusCode != 200 || capturedBody.Length == 0)
            {
                SetWarmupResult(context, WarmupResultPassThrough);
                capturedBody.Position = 0;
                await capturedBody.CopyToAsync(originalBody).ConfigureAwait(false);
                return;
            }

            capturedBody.Position = 0;

            (Stream resultStream, string contentType) result;
            try
            {
                result = await overlayService.AddBadgeOverlaysAsync(capturedBody, visibleBadges, imageConfig).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to add badge overlay, serving original image");
                SetWarmupResult(context, WarmupResultOverlayError);
                capturedBody.Position = 0;
                await capturedBody.CopyToAsync(originalBody).ConfigureAwait(false);
                return;
            }

            await using (result.resultStream.ConfigureAwait(false))
            {
                result.resultStream.Position = 0;
                var cached = await cacheService.CacheImageAsync(itemId, badgeKey, imageTag, badgeState, result.resultStream).ConfigureAwait(false);
                if (cached)
                {
                    cacheService.SetRequestCacheEntry(requestCacheKey, itemId, badgeKey, imageTag, badgeState);
                }

                SetWarmupResult(context, cached ? WarmupResultCacheWritten : WarmupResultCacheWriteFailed);

                result.resultStream.Position = 0;
                context.Response.ContentType = result.contentType;
                context.Response.ContentLength = result.resultStream.Length;
                await result.resultStream.CopyToAsync(originalBody).ConfigureAwait(false);
            }
        }
        finally
        {
            context.Response.Body = originalBody;
            if (renderLockAcquired)
            {
                renderLock.Semaphore.Release();
            }

            ReleaseRenderLock(renderKey, renderLock);
        }
    }

    private static void SetWarmupResult(HttpContext context, string result)
    {
        if (IsWarmupRequest(context.Request.Query) && !context.Response.HasStarted)
        {
            context.Response.Headers[WarmupResultHeader] = result;
        }
    }

    private static RenderLockState RentRenderLock(string key)
    {
        lock (RenderLocksLock)
        {
            if (!RenderLocks.TryGetValue(key, out var state))
            {
                state = new RenderLockState();
                RenderLocks[key] = state;
                return state;
            }

            state.RefCount++;
            return state;
        }
    }

    private static void ReleaseRenderLock(string key, RenderLockState state)
    {
        lock (RenderLocksLock)
        {
            state.RefCount--;
            if (state.RefCount == 0)
            {
                RenderLocks.TryRemove(new KeyValuePair<string, RenderLockState>(key, state));
                state.Semaphore.Dispose();
            }
        }
    }

    private static async Task ServeCachedImageFileAsync(HttpContext context, CachedImageFile cachedImage)
    {
        context.Response.ContentType = cachedImage.ContentType;
        context.Response.ContentLength = cachedImage.Length;
        await context.Response.SendFileAsync(cachedImage.Path, context.RequestAborted).ConfigureAwait(false);
    }

    private sealed class RenderLockState
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int RefCount { get; set; } = 1;
    }


    private async Task TryForceImageRefreshAsync(BaseItem item, string imageType, string badgeState, bool hasVisibleBadges, IProviderManager providerManager, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.ForceImageRefresh != true || !TryParseImageType(imageType, out var parsedImageType)) return;

        if (item is not (Movie or Series or Season or Episode))
        {
            return;
        }

        EnsureForceRefreshStateLoaded();

        var refreshKey = $"{item.Id:N}:{parsedImageType}";
        if (ForceRefreshStates.TryGetValue(refreshKey, out var currentState) && currentState == badgeState) return;
        if (!hasVisibleBadges && !ForceRefreshStates.ContainsKey(refreshKey)) return;

        var refreshLock = ForceRefreshLocks.GetOrAdd(refreshKey, _ => new SemaphoreSlim(1, 1));
        if (!await refreshLock.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;

        byte[]? originalBytes = null;

        try
        {
            var originalPath = item.GetImagePath(parsedImageType, 0);
            if (string.IsNullOrWhiteSpace(originalPath) || !System.IO.File.Exists(originalPath)) return;

            originalBytes = await System.IO.File.ReadAllBytesAsync(originalPath, cancellationToken).ConfigureAwait(false);
            if (originalBytes.Length == 0 || IsStockRefreshImage(originalBytes)) return;

            await using (var stockStream = new MemoryStream(StockRefreshImage, writable: false))
                await providerManager.SaveImage(item, stockStream, "image/png", parsedImageType, null, cancellationToken).ConfigureAwait(false);

            var restored = await RestoreOriginalImageAsync(item, parsedImageType, originalBytes, GetImageMimeType(originalPath), providerManager, cancellationToken).ConfigureAwait(false);
            if (!restored)
            {
                await TryRestoreOriginalBytesToCurrentPathAsync(item, parsedImageType, originalBytes, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("Original image restore verification failed after force-refresh touch.");
            }

            ForceRefreshStates[refreshKey] = badgeState;
            SaveForceRefreshState();
            _logger.LogInformation("Force-refreshed {ImageType} image metadata for {ItemName}", parsedImageType, item.Name);
        }
        catch (Exception ex)
        {
            if (originalBytes != null)
            {
                await TryRestoreOriginalBytesToCurrentPathAsync(item, parsedImageType, originalBytes, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogWarning(ex, "Failed to force-refresh {ImageType} image metadata for {ItemName}", imageType, item.Name);
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private static async Task<bool> RestoreOriginalImageAsync(BaseItem item, ImageType imageType, byte[] originalBytes, string mimeType, IProviderManager providerManager, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await using (var originalStream = new MemoryStream(originalBytes, writable: false))
            {
                await providerManager.SaveImage(item, originalStream, mimeType, imageType, null, cancellationToken).ConfigureAwait(false);
            }

            if (await IsRestoredImageValidAsync(item, imageType, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (attempt < 3)
            {
                await Task.Delay(200 * attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    private static async Task<bool> IsRestoredImageValidAsync(BaseItem item, ImageType imageType, CancellationToken cancellationToken)
    {
        var restoredPath = item.GetImagePath(imageType, 0);
        if (string.IsNullOrWhiteSpace(restoredPath) || !System.IO.File.Exists(restoredPath))
        {
            return false;
        }

        var restoredBytes = await System.IO.File.ReadAllBytesAsync(restoredPath, cancellationToken).ConfigureAwait(false);
        return restoredBytes.Length > StockRefreshImage.Length * 2 && !IsStockRefreshImage(restoredBytes);
    }

    private static async Task TryRestoreOriginalBytesToCurrentPathAsync(BaseItem item, ImageType imageType, byte[] originalBytes, CancellationToken cancellationToken)
    {
        try
        {
            var currentPath = item.GetImagePath(imageType, 0);
            if (!string.IsNullOrWhiteSpace(currentPath))
            {
                await System.IO.File.WriteAllBytesAsync(currentPath, originalBytes, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best-effort emergency restore only.
        }
    }

    private static bool IsStockRefreshImage(byte[] bytes)
    {
        return bytes.AsSpan().SequenceEqual(StockRefreshImage);
    }


    private static bool TryParseImageType(string imageType, out ImageType parsedImageType)
    {
        parsedImageType = ImageType.Primary;
        if (string.Equals(imageType, "Primary", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(imageType, "Thumb", StringComparison.OrdinalIgnoreCase)) { parsedImageType = ImageType.Thumb; return true; }
        return false;
    }

    private static string GetImageMimeType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "image/jpeg"
        };
    }


    private static string GetCacheRelevantQuery(IQueryCollection query)
    {
        var parts = query
            .Where(kvp => !string.Equals(kvp.Key, "tag", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(kvp.Key, "api_key", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(kvp.Key, "jellytagwarm", StringComparison.OrdinalIgnoreCase))
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => $"{kvp.Key}={string.Join(",", kvp.Value.ToArray())}");
        return string.Join("&", parts);
    }

    private static bool IsWarmupRequest(IQueryCollection query)
    {
        return query.TryGetValue("jellytagwarm", out var value)
            && value.Any(v => string.Equals(v, "1", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<SessionInfo?> GetRequestSessionInfoAsync(HttpContext context, AuthorizationInfo authorizationInfo, ISessionManager sessionManager)
    {
        if (!string.IsNullOrWhiteSpace(authorizationInfo.DeviceId) && !string.IsNullOrWhiteSpace(authorizationInfo.Client))
        {
            var existingSession = sessionManager.GetSession(authorizationInfo.DeviceId, authorizationInfo.Client, authorizationInfo.Version ?? string.Empty);
            if (existingSession != null)
            {
                return existingSession;
            }
        }

        if (string.IsNullOrWhiteSpace(authorizationInfo.Token))
        {
            return null;
        }

        try
        {
            var tokenSession = await sessionManager.GetSessionByAuthenticationToken(
                authorizationInfo.Token,
                authorizationInfo.DeviceId ?? string.Empty,
                context.Connection.RemoteIpAddress?.ToString() ?? string.Empty).ConfigureAwait(false);
            if (HasSessionSourceDetails(tokenSession))
            {
                return tokenSession;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to resolve JellyTag-Plus learned variant source from Jellyfin session data");
        }

        return GetSingleMatchingRecentSession(context, authorizationInfo, sessionManager);
    }

    private static SessionInfo? GetSingleMatchingRecentSession(HttpContext context, AuthorizationInfo authorizationInfo, ISessionManager sessionManager)
    {
        var remoteIp = context.Connection.RemoteIpAddress?.ToString();
        if (string.IsNullOrWhiteSpace(remoteIp))
        {
            return null;
        }

        var client = FirstNonBlank(authorizationInfo.Client, InferClientFromUserAgent(context.Request.Headers.UserAgent.ToString()));
        if (string.IsNullOrWhiteSpace(client))
        {
            return null;
        }

        var cutoff = DateTime.UtcNow.AddMinutes(-5);
        var matches = sessionManager.Sessions
            .Where(session => session.LastActivityDate >= cutoff)
            .Where(session => string.Equals(session.Client, client, StringComparison.OrdinalIgnoreCase))
            .Where(session => RemoteEndpointMatches(session.RemoteEndPoint, remoteIp))
            .Where(session => authorizationInfo.UserId == Guid.Empty || session.UserId == authorizationInfo.UserId)
            .Where(session => string.IsNullOrWhiteSpace(authorizationInfo.DeviceId)
                || string.Equals(session.DeviceId, authorizationInfo.DeviceId, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    private static bool HasSessionSourceDetails(SessionInfo? sessionInfo)
    {
        return sessionInfo != null
            && (!string.IsNullOrWhiteSpace(sessionInfo.UserName)
                || sessionInfo.UserId != Guid.Empty
                || !string.IsNullOrWhiteSpace(sessionInfo.DeviceName)
                || !string.IsNullOrWhiteSpace(sessionInfo.DeviceId));
    }

    private static bool RemoteEndpointMatches(string? remoteEndPoint, string remoteIp)
    {
        return !string.IsNullOrWhiteSpace(remoteEndPoint)
            && (string.Equals(remoteEndPoint, remoteIp, StringComparison.OrdinalIgnoreCase)
                || remoteEndPoint.StartsWith(remoteIp + ":", StringComparison.OrdinalIgnoreCase)
                || remoteEndPoint.StartsWith("[" + remoteIp + "]:", StringComparison.OrdinalIgnoreCase));
    }

    private static string FirstNonBlank(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static string InferClientFromUserAgent(string userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return string.Empty;
        }

        var value = userAgent.ToLowerInvariant();
        if (value.Contains("jellyfin android tv", StringComparison.Ordinal) || value.Contains("jellyfin-androidtv", StringComparison.Ordinal))
        {
            return "Jellyfin Android TV";
        }

        if (value.Contains("streamyfin", StringComparison.Ordinal))
        {
            return "Streamyfin";
        }

        if (value.Contains("findroid", StringComparison.Ordinal))
        {
            return "Findroid";
        }

        if (value.Contains("swiftfin", StringComparison.Ordinal))
        {
            return "Swiftfin";
        }

        if (value.Contains("iphone", StringComparison.Ordinal)
            || value.Contains("ipad", StringComparison.Ordinal)
            || value.Contains("ios", StringComparison.Ordinal)
            || (value.Contains("cfnetwork", StringComparison.Ordinal) && value.Contains("darwin", StringComparison.Ordinal)))
        {
            return "Jellyfin iOS";
        }

        if (value.Contains("android", StringComparison.Ordinal))
        {
            return "Jellyfin Android";
        }

        return string.Empty;
    }

    private static bool IsBypassRequest(IQueryCollection query)
    {
        return query.TryGetValue("jellytag", out var value)
            && value.Any(v =>
                string.Equals(v, "off", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, "0", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetImageVersion(BaseItem item, string imageType)
    {
        if (TryParseImageType(imageType, out var parsedImageType))
        {
            try
            {
                var imageInfo = item.GetImageInfo(parsedImageType, 0);
                if (imageInfo != null)
                {
                    return imageInfo.DateModified.Ticks.ToString();
                }
            }
            catch
            {
            }
        }

        return item.DateModified.Ticks.ToString();
    }

    private static void EnsureForceRefreshStateLoaded()
    {
        if (ForceRefreshStateLoaded) return;
        lock (ForceRefreshStateFileLock)
        {
            if (ForceRefreshStateLoaded) return;
            var path = GetForceRefreshStatePath();
            if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
            {
                try
                {
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(path));
                    if (loaded != null)
                    {
                        foreach (var pair in loaded) ForceRefreshStates[pair.Key] = pair.Value;
                    }
                }
                catch
                {
                }
            }
            ForceRefreshStateLoaded = true;
        }
    }

    private static void SaveForceRefreshState()
    {
        lock (ForceRefreshStateFileLock)
        {
            var path = GetForceRefreshStatePath();
            if (string.IsNullOrWhiteSpace(path)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var snapshot = ForceRefreshStates.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            var tempPath = path + ".tmp";
            System.IO.File.WriteAllText(tempPath, JsonSerializer.Serialize(snapshot));
            System.IO.File.Move(tempPath, path, overwrite: true);
        }
    }

    private static string? GetForceRefreshStatePath()
    {
        var dataFolder = Plugin.Instance?.DataFolderPath;
        return string.IsNullOrWhiteSpace(dataFolder) ? null : Path.Combine(dataFolder, ForceRefreshStateFileName);
    }

    private static string GetBadgeStateFingerprint(IReadOnlyList<BadgeInfo> badges)
    {
        var state = string.Join("|", badges
            .OrderBy(b => b.Category)
            .ThenBy(b => b.BadgeKey, StringComparer.OrdinalIgnoreCase)
            .Select(b => $"{b.Category}:{b.BadgeKey}:{b.ResourceFileName}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(state));
        return Convert.ToHexString(hash)[..16];
    }

    private static bool ShouldShowCollectionBadgeForImage(BadgeInfo badge, ImageTypeConfig imageConfig, string imageType, BaseItem item)
    {
        if (badge.Category != BadgeCategory.Collection)
        {
            return true;
        }

        var rule = imageConfig.CollectionRules?.FirstOrDefault(r => string.Equals(NormalizeCollectionBadgeKey(r), badge.BadgeKey, StringComparison.OrdinalIgnoreCase));
        if (rule == null)
        {
            return !string.IsNullOrWhiteSpace(imageConfig.CollectionRegex);
        }

        if (item is Season)
        {
            return rule.ShowOnSeasonPosters;
        }

        var isThumb = IsThumbnailRequest(imageType, item);
        if (!isThumb)
        {
            return rule.ShowOnPosters;
        }

        return item is Episode ? rule.ShowOnEpisodeThumbnails : rule.ShowOnSeriesThumbnails;
    }

    private static bool IsThumbnailRequest(string imageType, BaseItem item)
    {
        var type = imageType.ToUpperInvariant();
        return type == "THUMB" || (type == "PRIMARY" && item is Episode);
    }

    private static string NormalizeCollectionBadgeKey(CollectionBadgeRule rule)
    {
        var source = !string.IsNullOrWhiteSpace(rule.Key)
            ? rule.Key
            : (!string.IsNullOrWhiteSpace(rule.Label) ? rule.Label : "collection");
        var normalized = Regex.Replace(source.Trim().ToLowerInvariant(), @"[^a-z0-9._-]+", "-").Trim('-', '.', '_');
        return string.IsNullOrWhiteSpace(normalized) ? "collection" : normalized;
    }

    private static bool ShouldShowBadgeForLibrary(BadgeInfo badge, PluginConfiguration config, IReadOnlyList<Folder> collectionFolders)
    {
        if (collectionFolders.Count == 0 || config.LibraryBadgeOptions == null || config.LibraryBadgeOptions.Count == 0)
        {
            return true;
        }

        foreach (var folder in collectionFolders)
        {
            var options = config.LibraryBadgeOptions.FirstOrDefault(o => string.Equals(o.LibraryId, folder.Id.ToString("N"), StringComparison.OrdinalIgnoreCase));
            if (options == null)
            {
                continue;
            }

            return badge.Category switch
            {
                BadgeCategory.Resolution => options.Resolution,
                BadgeCategory.Hdr or BadgeCategory.ThreeD => options.Hdr,
                BadgeCategory.VideoCodec => options.Codec,
                BadgeCategory.Audio => options.Audio,
                BadgeCategory.Language or BadgeCategory.Subtitle => options.Language,
                BadgeCategory.Collection => options.Collections,
                _ => true
            };
        }

        return true;
    }

    private static ImageTypeConfig? GetImageTypeConfig(PluginConfiguration config, string imageType, BaseItem item)
    {
        var type = imageType.ToUpperInvariant();

        var isThumb = type switch
        {
            "PRIMARY" when item is Episode => true,
            "THUMB" => true,
            _ => false
        };

        if (isThumb && config.ThumbnailSameAsPoster)
        {
            return ApplySizeReduction(config.PosterConfig, config.ThumbnailSizeReduction);
        }

        return type switch
        {
            "PRIMARY" when item is Episode => config.ThumbnailConfig,
            "PRIMARY" => config.PosterConfig,
            "THUMB" => config.ThumbnailConfig,
            _ => null
        };
    }

    private static ImageTypeConfig ApplySizeReduction(ImageTypeConfig source, int reduction)
    {
        if (reduction <= 0) return source;

        var clone = new ImageTypeConfig
        {
            Enabled = source.Enabled,
            ResolutionPanel = ClonePanelWithReduction(source.ResolutionPanel, reduction),
            HdrPanel = ClonePanelWithReduction(source.HdrPanel, reduction),
            CodecPanel = ClonePanelWithReduction(source.CodecPanel, reduction),
            AudioPanel = ClonePanelWithReduction(source.AudioPanel, reduction),
            LanguagePanel = ClonePanelWithReduction(source.LanguagePanel, reduction),
            CollectionPanel = ClonePanelWithReduction(source.CollectionPanel, reduction),
            CollectionRules = source.CollectionRules?.Select(r => new CollectionBadgeRule
            {
                Key = r.Key,
                Regex = r.Regex,
                Label = r.Label,
                ShowOnPosters = r.ShowOnPosters,
                ShowOnSeasonPosters = r.ShowOnSeasonPosters,
                ShowOnSeriesThumbnails = r.ShowOnSeriesThumbnails,
                ShowOnEpisodeThumbnails = r.ShowOnEpisodeThumbnails
            }).ToList() ?? new List<CollectionBadgeRule>(),
            CollectionRegex = source.CollectionRegex,
            CollectionBadgeText = source.CollectionBadgeText,
            ShowVostIndicator = source.ShowVostIndicator,
            VostBgColor = source.VostBgColor,
            VostTextColor = source.VostTextColor,
            VostBgOpacity = source.VostBgOpacity,
            VostCornerRadius = source.VostCornerRadius
        };
        return clone;
    }

    private static BadgePanelSettings ClonePanelWithReduction(BadgePanelSettings panel, int reduction)
    {
        return new BadgePanelSettings
        {
            Enabled = panel.Enabled,
            Position = panel.Position,
            ShowMode = panel.ShowMode,
            Layout = panel.Layout,
            GapPercent = panel.GapPercent,
            SizePercent = Math.Max(1, panel.SizePercent - reduction),
            MarginPercent = panel.MarginPercent,
            Style = panel.Style,
            Order = panel.Order,
            TextBgColor = panel.TextBgColor,
            TextBgOpacity = panel.TextBgOpacity,
            TextColor = panel.TextColor,
            TextCornerRadius = panel.TextCornerRadius,
            BadgeTypeOverrides = new List<BadgeTypeStyleOverride>(panel.BadgeTypeOverrides),
            EnabledBadges = new List<string>(panel.EnabledBadges)
        };
    }
}
