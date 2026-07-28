using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyTag.Configuration;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTag.Services;

/// <summary>
/// Resolves and indexes final JellyTag-Plus badge visibility for image requests.
/// </summary>
public class BadgeVisibilityService : IBadgeVisibilityService
{
    private const string VisibleBadgeStateIndexFileName = "visible-badge-state-index.json";
    private const int VisibleBadgeStateIndexVersion = 2;
    private static readonly TimeSpan VisibleBadgeCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly string EmptyBadgeState = CreateBadgeStateFingerprint(Array.Empty<BadgeInfo>());

    private readonly ILibraryManager _libraryManager;
    private readonly IQualityDetectionService _qualityDetectionService;
    private readonly IImageOverlayService _overlayService;
    private readonly ILogger<BadgeVisibilityService> _logger;
    private readonly object _indexLock = new();
    private readonly ConcurrentDictionary<string, VisibleBadgeCacheEntry> _visibleBadgeCache = new();
    private VisibleBadgeStateIndex? _visibleBadgeStateIndex;
    private bool _visibleBadgeStateIndexLoaded;

    /// <summary>
    /// Initializes a new instance of the <see cref="BadgeVisibilityService"/> class.
    /// </summary>
    public BadgeVisibilityService(
        ILibraryManager libraryManager,
        IQualityDetectionService qualityDetectionService,
        IImageOverlayService overlayService,
        ILogger<BadgeVisibilityService> logger)
    {
        _libraryManager = libraryManager;
        _qualityDetectionService = qualityDetectionService;
        _overlayService = overlayService;
        _logger = logger;
    }

    /// <inheritdoc />
    public ImageTypeConfig? GetImageTypeConfig(PluginConfiguration config, string imageType, BaseItem item)
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

    /// <inheritdoc />
    public string GetImageTargetKey(string imageType, BaseItem item)
    {
        if (item is Season)
        {
            return "season-poster";
        }

        if (IsVideoTarget(item))
        {
            return "video";
        }

        if (IsOtherTarget(item))
        {
            return "other";
        }

        if (!IsThumbnailRequest(imageType, item))
        {
            return "poster";
        }

        return item is Episode ? "episode-thumbnail" : "series-thumbnail";
    }

    /// <inheritdoc />
    public VisibleBadgeState GetVisibleBadgeState(BaseItem item, string imageType, string imageVersion, ImageTypeConfig imageConfig, PluginConfiguration config, IReadOnlyList<Folder> collectionFolders, string targetConfigFingerprint)
    {
        var cacheKey = GetVisibleBadgeCacheKey(item, imageType, imageVersion, imageConfig, config, collectionFolders);
        if (_visibleBadgeCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.CachedAt < VisibleBadgeCacheTtl)
        {
            return cached.State with { Badges = cached.State.Badges.Select(CloneBadgeInfo).ToList() };
        }

        var state = CalculateVisibleBadgeState(item, imageType, imageConfig, config, collectionFolders);
        _visibleBadgeCache[cacheKey] = new VisibleBadgeCacheEntry(state with { Badges = state.Badges.Select(CloneBadgeInfo).ToList() }, DateTime.UtcNow);
        PruneVisibleBadgeCache();
        UpsertVisibleBadgeState(item, imageType, imageVersion, config, collectionFolders, targetConfigFingerprint, state);
        return state;
    }

    /// <inheritdoc />
    public VisibleBadgeState? TryGetIndexedVisibleBadgeState(BaseItem item, string imageType, string imageVersion, PluginConfiguration config, IReadOnlyList<Folder> collectionFolders, string targetConfigFingerprint)
    {
        var key = GetVisibleBadgeStateStableKey(item, imageType);
        var libraryIds = GetLibraryIdsFingerprint(collectionFolders);
        lock (_indexLock)
        {
            var index = GetVisibleBadgeStateIndexLocked();
            if (!index.Entries.TryGetValue(key, out var entry))
            {
                return null;
            }

            if (!string.Equals(entry.ImageVersion, imageVersion, StringComparison.Ordinal)
                || entry.ItemModifiedTicks != item.DateModified.Ticks
                || !string.Equals(entry.TargetConfigFingerprint, targetConfigFingerprint, StringComparison.Ordinal)
                || !string.Equals(entry.LibraryIdsFingerprint, libraryIds, StringComparison.Ordinal))
            {
                return null;
            }

            return new VisibleBadgeState([], entry.BadgeState, entry.BadgeKey, entry.HasVisibleBadges);
        }
    }

    /// <inheritdoc />
    public async Task RefreshBadgeStatusIndexAsync(Action<double>? progress, Func<BaseItem, string, VisibleBadgeState, bool, CancellationToken, Task>? changedCallback, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _qualityDetectionService.RefreshBadgeStatusIndexAsync(percent => progress?.Invoke(Math.Clamp(percent * 0.45, 0, 0.45)), cancellationToken).ConfigureAwait(false);

        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.Enabled)
        {
            progress?.Invoke(1);
            return;
        }

        var items = GetBadgeStatusIndexItems();
        var imageTypes = new[] { "Primary", "Thumb" };
        var totalWork = Math.Max(1, items.Count * imageTypes.Length);
        var completed = 0;
        var pendingEntries = new Dictionary<string, VisibleBadgeStateIndexEntry>(StringComparer.OrdinalIgnoreCase);
        var forceRefreshExistingEntriesForIndexUpgrade = false;
        lock (_indexLock)
        {
            forceRefreshExistingEntriesForIndexUpgrade = GetVisibleBadgeStateIndexLocked().Version < VisibleBadgeStateIndexVersion;
        }

        foreach (var item in items)
        {
            var collectionFolders = _libraryManager.GetCollectionFolders(item).ToList();
            var isExcluded = IsExcludedLibrary(config, collectionFolders);

            foreach (var imageType in imageTypes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var imageConfig = GetImageTypeConfig(config, imageType, item);
                var imageVersion = GetImageVersion(item, imageType);
                var targetKey = GetImageTargetKey(imageType, item);
                var targetConfigFingerprint = CreateTargetConfigFingerprint(config, imageType, targetKey);
                var state = imageConfig == null || !imageConfig.Enabled || isExcluded
                    ? CreateEmptyVisibleBadgeState()
                    : CalculateVisibleBadgeState(item, imageType, imageConfig, config, collectionFolders);
                var stableKey = GetVisibleBadgeStateStableKey(item, imageType);
                var entry = CreateIndexEntry(item, imageType, imageVersion, targetKey, targetConfigFingerprint, collectionFolders, state);

                VisibleBadgeStateIndexEntry? oldEntry;
                lock (_indexLock)
                {
                    var index = GetVisibleBadgeStateIndexLocked();
                    index.Entries.TryGetValue(stableKey, out oldEntry);
                }

                pendingEntries[stableKey] = entry;
                var shouldRefreshImageMetadata = oldEntry == null
                    || forceRefreshExistingEntriesForIndexUpgrade
                    || !string.Equals(oldEntry.BadgeState, state.BadgeState, StringComparison.Ordinal);
                if (shouldRefreshImageMetadata && changedCallback != null)
                {
                    await changedCallback(item, imageType, state, oldEntry == null || forceRefreshExistingEntriesForIndexUpgrade, cancellationToken).ConfigureAwait(false);
                }

                completed++;
                if (completed % 25 == 0)
                {
                    progress?.Invoke(0.45 + Math.Clamp(completed * 0.55 / totalWork, 0, 0.55));
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        ReplaceVisibleBadgeStateIndex(pendingEntries);
        progress?.Invoke(1);
    }

    private VisibleBadgeState CalculateVisibleBadgeState(BaseItem item, string imageType, ImageTypeConfig imageConfig, PluginConfiguration config, IReadOnlyList<Folder> collectionFolders)
    {
        var allBadges = _qualityDetectionService.DetectAllBadges(item, imageConfig);
        _logger.LogDebug("DetectAllBadges for {Item}: {Count} badges found: {Badges}",
            item.Name, allBadges.Count, string.Join(", ", allBadges.Select(b => $"{b.Category}:{b.BadgeKey}")));

        var visibleBadges = allBadges
            .Where(b => _overlayService.ShouldShowBadge(b, imageConfig))
            .Where(b => ShouldShowBadgeForImageTarget(b, imageConfig, imageType, item))
            .Where(b => ShouldShowCollectionBadgeForImage(b, imageConfig, imageType, item))
            .Where(b => ShouldShowBadgeForLibrary(b, config, collectionFolders))
            .ToList();

        var badgeState = CreateBadgeStateFingerprint(visibleBadges);
        var badgeKey = visibleBadges.Count == 0 ? string.Empty : string.Join("_", visibleBadges.Select(b => b.BadgeKey));
        return new VisibleBadgeState(visibleBadges, badgeState, badgeKey, visibleBadges.Count > 0);
    }

    private static VisibleBadgeState CreateEmptyVisibleBadgeState()
    {
        return new VisibleBadgeState([], EmptyBadgeState, string.Empty, false);
    }

    private void UpsertVisibleBadgeState(BaseItem item, string imageType, string imageVersion, PluginConfiguration config, IReadOnlyList<Folder> collectionFolders, string targetConfigFingerprint, VisibleBadgeState state)
    {
        var key = GetVisibleBadgeStateStableKey(item, imageType);
        var entry = CreateIndexEntry(item, imageType, imageVersion, GetImageTargetKey(imageType, item), targetConfigFingerprint, collectionFolders, state);
        lock (_indexLock)
        {
            var index = GetVisibleBadgeStateIndexLocked();
            if (index.Entries.TryGetValue(key, out var existing)
                && string.Equals(existing.BadgeState, entry.BadgeState, StringComparison.Ordinal)
                && string.Equals(existing.ImageVersion, entry.ImageVersion, StringComparison.Ordinal)
                && existing.ItemModifiedTicks == entry.ItemModifiedTicks
                && string.Equals(existing.TargetConfigFingerprint, entry.TargetConfigFingerprint, StringComparison.Ordinal)
                && string.Equals(existing.LibraryIdsFingerprint, entry.LibraryIdsFingerprint, StringComparison.Ordinal)
                && existing.HasVisibleBadges == entry.HasVisibleBadges)
            {
                return;
            }

            index.Entries[key] = entry;
            SaveVisibleBadgeStateIndexLocked();
        }
    }

    private void ReplaceVisibleBadgeStateIndex(Dictionary<string, VisibleBadgeStateIndexEntry> entries)
    {
        lock (_indexLock)
        {
            _visibleBadgeStateIndex = new VisibleBadgeStateIndex
            {
                Version = VisibleBadgeStateIndexVersion,
                Entries = entries,
                CompletedUtcTicks = DateTime.UtcNow.Ticks
            };
            _visibleBadgeStateIndexLoaded = true;
            SaveVisibleBadgeStateIndexLocked();
        }
    }

    private VisibleBadgeStateIndex GetVisibleBadgeStateIndexLocked()
    {
        if (_visibleBadgeStateIndexLoaded)
        {
            return _visibleBadgeStateIndex ??= new VisibleBadgeStateIndex();
        }

        _visibleBadgeStateIndexLoaded = true;
        var path = GetVisibleBadgeStateIndexPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _visibleBadgeStateIndex = new VisibleBadgeStateIndex();
            return _visibleBadgeStateIndex;
        }

        try
        {
            _visibleBadgeStateIndex = JsonSerializer.Deserialize<VisibleBadgeStateIndex>(File.ReadAllText(path)) ?? new VisibleBadgeStateIndex();
            _visibleBadgeStateIndex.Entries ??= new Dictionary<string, VisibleBadgeStateIndexEntry>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load JellyTag-Plus visible badge state index; it will be rebuilt as images are used");
            _visibleBadgeStateIndex = new VisibleBadgeStateIndex();
        }

        return _visibleBadgeStateIndex;
    }

    private void SaveVisibleBadgeStateIndexLocked()
    {
        var path = GetVisibleBadgeStateIndexPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(GetVisibleBadgeStateIndexLocked()));
        File.Move(tempPath, path, overwrite: true);
    }

    private static string? GetVisibleBadgeStateIndexPath()
    {
        var dataFolder = Plugin.Instance?.DataFolderPath;
        return string.IsNullOrWhiteSpace(dataFolder) ? null : Path.Combine(dataFolder, VisibleBadgeStateIndexFileName);
    }

    private static VisibleBadgeStateIndexEntry CreateIndexEntry(BaseItem item, string imageType, string imageVersion, string targetKey, string targetConfigFingerprint, IReadOnlyList<Folder> collectionFolders, VisibleBadgeState state)
    {
        return new VisibleBadgeStateIndexEntry
        {
            ItemId = item.Id.ToString("N"),
            ImageType = imageType,
            TargetKey = targetKey,
            ImageVersion = imageVersion,
            ItemModifiedTicks = item.DateModified.Ticks,
            TargetConfigFingerprint = targetConfigFingerprint,
            LibraryIdsFingerprint = GetLibraryIdsFingerprint(collectionFolders),
            BadgeState = state.BadgeState,
            BadgeKey = state.BadgeKey,
            HasVisibleBadges = state.HasVisibleBadges,
            UpdatedUtcTicks = DateTime.UtcNow.Ticks
        };
    }

    private static string GetVisibleBadgeStateStableKey(BaseItem item, string imageType)
    {
        return $"{item.Id:N}:{imageType.ToUpperInvariant()}";
    }

    private static string GetVisibleBadgeCacheKey(BaseItem item, string imageType, string imageVersion, ImageTypeConfig imageConfig, PluginConfiguration config, IReadOnlyList<Folder> collectionFolders)
    {
        var configJson = JsonSerializer.Serialize(new
        {
            config.Enabled,
            LibraryBadgeOptions = config.LibraryBadgeOptions?.OrderBy(option => option.LibraryId, StringComparer.OrdinalIgnoreCase),
            ImageConfig = imageConfig,
            config.CustomBadgeTexts
        });
        var input = $"{item.Id:N}|{imageType}|{imageVersion}|{item.DateModified.Ticks}|{GetLibraryIdsFingerprint(collectionFolders)}|{configJson}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes);
    }

    private static string CreateBadgeStateFingerprint(IReadOnlyList<BadgeInfo> badges)
    {
        var state = string.Join("|", badges
            .OrderBy(b => b.Category)
            .ThenBy(b => b.BadgeKey, StringComparer.OrdinalIgnoreCase)
            .Select(b => $"{b.Category}:{b.BadgeKey}:{b.ResourceFileName}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(state));
        return Convert.ToHexString(hash)[..16];
    }

    private static string CreateTargetConfigFingerprint(PluginConfiguration config, string imageType, string targetKey)
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
        if (ShouldUseThumbnailConfig(imageType, targetKey))
        {
            AppendImageTypeFingerprint(sb, config.ThumbnailSameAsPoster ? config.PosterConfig : config.ThumbnailConfig, targetKey);
        }
        else
        {
            AppendImageTypeFingerprint(sb, config.PosterConfig, targetKey);
        }

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

    private static bool ShouldUseThumbnailConfig(string imageType, string? targetKey)
    {
        return string.Equals(imageType, "Thumb", StringComparison.OrdinalIgnoreCase)
            || string.Equals(targetKey, "episode-thumbnail", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendImageTypeFingerprint(StringBuilder sb, ImageTypeConfig c, string? targetKey = null)
    {
        sb.Append(c.Enabled).Append('|');
        AppendPanelFingerprint(sb, c.ResolutionPanel, targetKey);
        AppendPanelFingerprint(sb, c.HdrPanel, targetKey);
        AppendPanelFingerprint(sb, c.CodecPanel, targetKey);
        AppendPanelFingerprint(sb, c.AudioPanel, targetKey);
        AppendPanelFingerprint(sb, c.LanguagePanel, targetKey);
        AppendPanelFingerprint(sb, c.CollectionPanel, targetKey);
        sb.Append(c.CollectionRegex ?? "n").Append(c.CollectionBadgeText ?? "n").Append('|');
        if (c.CollectionRules != null)
        {
            foreach (var rule in c.CollectionRules)
            {
                sb.Append(rule.Key).Append('=').Append(rule.Regex).Append('=').Append(rule.Label)
                    .Append('=').Append(rule.Position?.ToString() ?? "default")
                    .Append('=').Append(rule.Layout?.ToString() ?? "default")
                    .Append('=');
                AppendTargetValue(sb, targetKey, rule.ShowOnPosters, rule.ShowOnSeasonPosters, rule.ShowOnSeriesThumbnails, rule.ShowOnEpisodeThumbnails, rule.ShowOnVideos, rule.ShowOnOther);
                sb.Append(',');
            }
        }
        sb.Append('|');
        sb.Append(c.ShowVostIndicator).Append(c.VostBgColor ?? "n").Append(c.VostTextColor ?? "n");
        sb.Append(c.VostBgOpacity).Append(c.VostCornerRadius).Append('|');
    }

    private static void AppendPanelFingerprint(StringBuilder sb, BadgePanelSettings p, string? targetKey = null)
    {
        sb.Append(p.Enabled).Append((int)p.Position).Append((int)p.ShowMode);
        AppendTargetValue(sb, targetKey, p.ShowOnPosters, p.ShowOnSeasonPosters, p.ShowOnSeriesThumbnails, p.ShowOnEpisodeThumbnails, p.ShowOnVideos, p.ShowOnOther);
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

    private static void AppendTargetValue(StringBuilder sb, string? targetKey, bool? posters, bool? seasonPosters, bool? seriesThumbnails, bool? episodeThumbnails, bool? videos, bool? other)
    {
        switch (targetKey)
        {
            case "poster":
                sb.Append(posters);
                break;
            case "season-poster":
                sb.Append(seasonPosters);
                break;
            case "series-thumbnail":
                sb.Append(seriesThumbnails);
                break;
            case "episode-thumbnail":
                sb.Append(episodeThumbnails);
                break;
            case "video":
                sb.Append(videos);
                break;
            case "other":
                sb.Append(other);
                break;
            default:
                sb.Append(posters).Append(seasonPosters).Append(seriesThumbnails).Append(episodeThumbnails).Append(videos).Append(other);
                break;
        }
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

        if (IsVideoTarget(item))
        {
            return rule.ShowOnVideos;
        }

        if (IsOtherTarget(item))
        {
            return rule.ShowOnOther;
        }

        var isThumb = IsThumbnailRequest(imageType, item);
        if (!isThumb)
        {
            return rule.ShowOnPosters;
        }

        return item is Episode ? rule.ShowOnEpisodeThumbnails : rule.ShowOnSeriesThumbnails;
    }

    private static bool ShouldShowBadgeForImageTarget(BadgeInfo badge, ImageTypeConfig imageConfig, string imageType, BaseItem item)
    {
        if (badge.Category == BadgeCategory.Collection)
        {
            return true;
        }

        return IsPanelTargetEnabled(GetPanelForBadgeCategory(badge.Category, imageConfig), imageType, item);
    }

    private static BadgePanelSettings GetPanelForBadgeCategory(BadgeCategory category, ImageTypeConfig imageConfig)
    {
        return category switch
        {
            BadgeCategory.Resolution => imageConfig.ResolutionPanel,
            BadgeCategory.Hdr or BadgeCategory.ThreeD => imageConfig.HdrPanel,
            BadgeCategory.VideoCodec => imageConfig.CodecPanel,
            BadgeCategory.Audio => imageConfig.AudioPanel,
            BadgeCategory.Language or BadgeCategory.Subtitle => imageConfig.LanguagePanel,
            BadgeCategory.Collection => imageConfig.CollectionPanel,
            _ => imageConfig.ResolutionPanel
        };
    }

    private static bool IsPanelTargetEnabled(BadgePanelSettings panel, string imageType, BaseItem item)
    {
        if (!panel.Enabled)
        {
            return false;
        }

        if (item is Season)
        {
            return panel.ShowOnSeasonPosters ?? panel.Enabled;
        }

        if (IsVideoTarget(item))
        {
            return panel.ShowOnVideos ?? panel.Enabled;
        }

        if (IsOtherTarget(item))
        {
            return panel.ShowOnOther ?? panel.Enabled;
        }

        var isThumb = IsThumbnailRequest(imageType, item);
        if (!isThumb)
        {
            return panel.ShowOnPosters ?? panel.Enabled;
        }

        return item is Episode
            ? panel.ShowOnEpisodeThumbnails ?? panel.Enabled
            : panel.ShowOnSeriesThumbnails ?? panel.Enabled;
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

    private static ImageTypeConfig ApplySizeReduction(ImageTypeConfig source, int reduction)
    {
        if (reduction <= 0) return source;

        var clone = new ImageTypeConfig
        {
            Enabled = source.Enabled,
            CollectionRegex = source.CollectionRegex,
            CollectionBadgeText = source.CollectionBadgeText,
            CollectionRules = source.CollectionRules,
            ShowVostIndicator = source.ShowVostIndicator,
            VostBgColor = source.VostBgColor,
            VostTextColor = source.VostTextColor,
            VostBgOpacity = source.VostBgOpacity,
            VostCornerRadius = source.VostCornerRadius,
            ResolutionPanel = ReducePanel(source.ResolutionPanel, reduction),
            HdrPanel = ReducePanel(source.HdrPanel, reduction),
            CodecPanel = ReducePanel(source.CodecPanel, reduction),
            AudioPanel = ReducePanel(source.AudioPanel, reduction),
            LanguagePanel = ReducePanel(source.LanguagePanel, reduction),
            CollectionPanel = ReducePanel(source.CollectionPanel, reduction)
        };

        return clone;
    }

    private static BadgePanelSettings ReducePanel(BadgePanelSettings panel, int reduction)
    {
        return new BadgePanelSettings
        {
            Enabled = panel.Enabled,
            ShowOnPosters = panel.ShowOnPosters,
            ShowOnSeasonPosters = panel.ShowOnSeasonPosters,
            ShowOnSeriesThumbnails = panel.ShowOnSeriesThumbnails,
            ShowOnEpisodeThumbnails = panel.ShowOnEpisodeThumbnails,
            ShowOnVideos = panel.ShowOnVideos,
            ShowOnOther = panel.ShowOnOther,
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
            EnabledBadges = new List<string>(panel.EnabledBadges),
            BadgeTypeOverrides = panel.BadgeTypeOverrides.Select(o => new BadgeTypeStyleOverride
            {
                BadgeKey = o.BadgeKey,
                BgColor = o.BgColor,
                BgOpacity = o.BgOpacity,
                TextColor = o.TextColor,
                CornerRadius = o.CornerRadius
            }).ToList()
        };
    }

    private static bool IsExcludedLibrary(PluginConfiguration config, IReadOnlyList<Folder> collectionFolders)
    {
        return config.ExcludedLibraryIds?.Count > 0
            && collectionFolders.Any(f => config.ExcludedLibraryIds.Contains(f.Id.ToString("N")));
    }

    private List<BaseItem> GetBadgeStatusIndexItems()
    {
        try
        {
            return _libraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes =
                [
                    BaseItemKind.Movie,
                    BaseItemKind.Series,
                    BaseItemKind.Season,
                    BaseItemKind.Episode,
                    BaseItemKind.Video,
                    BaseItemKind.MusicVideo
                ]
            })
            .Where(item => item is Movie or Series or Season or Episode or Video)
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list items for JellyTag-Plus visible badge state index");
            return [];
        }
    }

    private static string GetImageVersion(BaseItem item, string imageType)
    {
        if (!Enum.TryParse<ImageType>(imageType, true, out var parsedType))
        {
            return item.DateModified.Ticks.ToString();
        }

        var info = item.GetImageInfo(parsedType, 0);
        if (info != null)
        {
            return info.DateModified.Ticks.ToString();
        }

        return item.DateModified.Ticks.ToString();
    }

    private static string GetLibraryIdsFingerprint(IReadOnlyList<Folder> collectionFolders)
    {
        return string.Join(",", collectionFolders.Select(folder => folder.Id.ToString("N")).OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
    }

    private static BadgeInfo CloneBadgeInfo(BadgeInfo badge)
    {
        return new BadgeInfo
        {
            BadgeKey = badge.BadgeKey,
            Category = badge.Category,
            ResourceFileName = badge.ResourceFileName
        };
    }

    private void PruneVisibleBadgeCache()
    {
        var cutoff = DateTime.UtcNow - VisibleBadgeCacheTtl;
        foreach (var key in _visibleBadgeCache
            .Where(kvp => kvp.Value.CachedAt < cutoff)
            .Select(kvp => kvp.Key)
            .Take(100)
            .ToArray())
        {
            _visibleBadgeCache.TryRemove(key, out _);
        }
    }

    private static bool IsVideoTarget(BaseItem item)
    {
        return item is not (Movie or Series or Season or Episode)
            && (item is MusicVideo || item.GetType() == typeof(Video));
    }

    private static bool IsOtherTarget(BaseItem item)
    {
        return item is not (Movie or Series or Season or Episode or MusicVideo)
            && item.GetType() != typeof(Video);
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

    private sealed record VisibleBadgeCacheEntry(VisibleBadgeState State, DateTime CachedAt);

    private sealed class VisibleBadgeStateIndex
    {
        public int Version { get; set; } = VisibleBadgeStateIndexVersion;

        public Dictionary<string, VisibleBadgeStateIndexEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public long CompletedUtcTicks { get; set; }
    }

    private sealed class VisibleBadgeStateIndexEntry
    {
        public string ItemId { get; set; } = string.Empty;

        public string ImageType { get; set; } = string.Empty;

        public string TargetKey { get; set; } = string.Empty;

        public string ImageVersion { get; set; } = string.Empty;

        public long ItemModifiedTicks { get; set; }

        public string TargetConfigFingerprint { get; set; } = string.Empty;

        public string LibraryIdsFingerprint { get; set; } = string.Empty;

        public string BadgeState { get; set; } = EmptyBadgeState;

        public string BadgeKey { get; set; } = string.Empty;

        public bool HasVisibleBadges { get; set; }

        public long UpdatedUtcTicks { get; set; }
    }
}
