using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyTag.Configuration;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using VideoRange = Jellyfin.Data.Enums.VideoRange;
using VideoRangeType = Jellyfin.Data.Enums.VideoRangeType;

namespace Jellyfin.Plugin.JellyTag.Services;

/// <summary>
/// Service for detecting video quality from media items.
/// </summary>
public class QualityDetectionService : IQualityDetectionService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<QualityDetectionService> _logger;
    private readonly ConcurrentDictionary<Guid, (List<BadgeInfo> Badges, DateTime CachedAt)> _badgeCache = new();
    private static readonly TimeSpan BadgeCacheTtl = TimeSpan.FromMinutes(5);
    private DateTime _lastCacheCleanup = DateTime.UtcNow;
    private static readonly TimeSpan CacheCleanupInterval = TimeSpan.FromMinutes(10);

    public QualityDetectionService(
        ILibraryManager libraryManager,
        ILogger<QualityDetectionService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public VideoQuality GetQuality(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item == null)
        {
            _logger.LogDebug("Item not found: {ItemId}", itemId);
            return VideoQuality.Unknown;
        }

        return GetQualityFromItem(item);
    }

    public static VideoQuality DetermineQuality(int width, int height)
    {
        var maxDimension = Math.Max(width, height);

        if (maxDimension >= 3800) return VideoQuality.UHD4K;
        if (maxDimension >= 1900) return VideoQuality.FHD1080p;
        if (maxDimension >= 1260) return VideoQuality.HD720p;
        if (maxDimension > 0) return VideoQuality.SD;
        return VideoQuality.Unknown;
    }

    /// <inheritdoc />
    public VideoQuality GetQualityFromItem(BaseItem item)
    {
        if (item is Video video)
        {
            return GetQualityFromVideo(video);
        }

        var query = new InternalItemsQuery
        {
            ParentId = item.Id,
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            Limit = 10
        };
        var children = _libraryManager.GetItemList(query);
        var bestQuality = VideoQuality.Unknown;
        foreach (var child in children)
        {
            if (child is Video childVideo)
            {
                var q = GetQualityFromVideo(childVideo);
                if (q != VideoQuality.Unknown && (bestQuality == VideoQuality.Unknown || q > bestQuality))
                {
                    bestQuality = q;
                    if (bestQuality == VideoQuality.UHD4K) break;
                }
            }
        }

        if (bestQuality != VideoQuality.Unknown)
        {
            _logger.LogDebug("Resolved quality {Quality} for parent item: {ItemName}", bestQuality, item.Name);
        }

        return bestQuality;
    }

    /// <inheritdoc />
    public List<BadgeInfo> DetectAllBadges(BaseItem item)
    {
        if (_badgeCache.TryGetValue(item.Id, out var cached) && DateTime.UtcNow - cached.CachedAt < BadgeCacheTtl)
        {
            return new List<BadgeInfo>(cached.Badges);
        }

        var badges = DetectAllBadgesInternal(item);
        _badgeCache[item.Id] = (badges, DateTime.UtcNow);

        // Periodically evict expired entries to prevent unbounded memory growth
        if (DateTime.UtcNow - _lastCacheCleanup > CacheCleanupInterval)
        {
            _lastCacheCleanup = DateTime.UtcNow;
            var expiredKeys = _badgeCache
                .Where(kvp => DateTime.UtcNow - kvp.Value.CachedAt > BadgeCacheTtl)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in expiredKeys)
            {
                _badgeCache.TryRemove(key, out _);
            }
        }
        return badges;
    }

    /// <inheritdoc />
    public void ClearBadgeCache()
    {
        _badgeCache.Clear();
    }

    private List<BadgeInfo> DetectAllBadgesInternal(BaseItem item)
    {
        var badges = new List<BadgeInfo>();

        DetectCollectionBadge(item, badges);

        if (item is Video video)
        {
            DetectBadgesFromVideo(video, badges);
        }
        else
        {
            var childVideos = GetChildVideos(item);

            var bestResolution = VideoQuality.Unknown;
            Video? bestVideo = null;

            foreach (var childVideo in childVideos)
            {
                var q = GetQualityFromVideo(childVideo);
                if (q != VideoQuality.Unknown && (bestResolution == VideoQuality.Unknown || q > bestResolution))
                {
                    bestResolution = q;
                    bestVideo = childVideo;
                }

                bestVideo ??= childVideo;
            }

            if (bestResolution != VideoQuality.Unknown)
            {
                badges.Add(CreateResolutionBadge(bestResolution));
            }

            if (bestVideo != null)
            {
                DetectHdrAndAudioBadges(bestVideo, badges, includeLanguages: false);
            }

            if (childVideos.Count > 0)
            {
                DetectLanguageBadgesFromVideos(childVideos, badges);
            }
        }

        return badges;
    }

    private List<Video> GetChildVideos(BaseItem item)
    {
        var query = new InternalItemsQuery
        {
            ParentId = item.Id,
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode]
        };

        var childVideos = _libraryManager.GetItemList(query).OfType<Video>().ToList();
        if (childVideos.Count > 0)
        {
            return childVideos;
        }

        var descendantQuery = new InternalItemsQuery
        {
            AncestorIds = [item.Id],
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode]
        };

        return _libraryManager.GetItemList(descendantQuery).OfType<Video>().ToList();
    }


    private void DetectCollectionBadge(BaseItem item, List<BadgeInfo> badges)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return;
        }

        var rules = GetCollectionRules(config).ToList();
        if (rules.Count == 0)
        {
            return;
        }

        var compiledRules = new List<(CollectionBadgeRule Rule, Regex Regex)>();
        foreach (var rule in rules)
        {
            try
            {
                compiledRules.Add((rule, new Regex(rule.Regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250))));
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "Invalid collection badge regex: {Regex}", rule.Regex);
            }
        }

        if (compiledRules.Count == 0)
        {
            return;
        }

        try
        {
            var addedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (item is Movie movie && !string.IsNullOrWhiteSpace(movie.TmdbCollectionName))
            {
                AddMatchingCollectionBadges(movie.TmdbCollectionName, compiledRules, addedKeys, badges);
            }

            var collections = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.BoxSet],
                Recursive = true
            });

            var collectionItems = GetCollectionCandidateItems(item).ToList();
            foreach (var collection in collections.OfType<BoxSet>())
            {
                if (!collectionItems.Any(candidate => CollectionContainsItem(collection, candidate)))
                {
                    continue;
                }

                AddMatchingCollectionBadges(collection.Name ?? string.Empty, compiledRules, addedKeys, badges);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to detect collection badge for item: {ItemName}", item.Name);
        }
    }

    private static IEnumerable<CollectionBadgeRule> GetCollectionRules(PluginConfiguration config)
    {
        var rules = new List<CollectionBadgeRule>();
        AddRules(config.PosterConfig, rules);
        AddRules(config.ThumbnailConfig, rules);

        return rules
            .Where(r => !string.IsNullOrWhiteSpace(r.Regex))
            .GroupBy(NormalizeCollectionBadgeKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());
    }

    private static void AddRules(ImageTypeConfig? imageConfig, List<CollectionBadgeRule> rules)
    {
        if (imageConfig == null)
        {
            return;
        }

        if (imageConfig.CollectionRules?.Count > 0)
        {
            rules.AddRange(imageConfig.CollectionRules);
            return;
        }

        if (!string.IsNullOrWhiteSpace(imageConfig.CollectionRegex))
        {
            rules.Add(new CollectionBadgeRule
            {
                Key = "collection",
                Regex = imageConfig.CollectionRegex,
                Label = string.IsNullOrWhiteSpace(imageConfig.CollectionBadgeText) ? "COLLECTION" : imageConfig.CollectionBadgeText
            });
        }
    }

    private static string NormalizeCollectionBadgeKey(CollectionBadgeRule rule)
    {
        var source = !string.IsNullOrWhiteSpace(rule.Key)
            ? rule.Key
            : (!string.IsNullOrWhiteSpace(rule.Label) ? rule.Label : "collection");
        var normalized = Regex.Replace(source.Trim().ToLowerInvariant(), @"[^a-z0-9._-]+", "-").Trim('-', '.', '_');
        return string.IsNullOrWhiteSpace(normalized) ? "collection" : normalized;
    }

    private static void AddMatchingCollectionBadges(
        string collectionName,
        List<(CollectionBadgeRule Rule, Regex Regex)> compiledRules,
        HashSet<string> addedKeys,
        List<BadgeInfo> badges)
    {
        foreach (var (rule, regex) in compiledRules)
        {
            if (!regex.IsMatch(collectionName))
            {
                continue;
            }

            var key = NormalizeCollectionBadgeKey(rule);
            if (!addedKeys.Add(key))
            {
                continue;
            }

            badges.Add(new BadgeInfo
            {
                Category = BadgeCategory.Collection,
                BadgeKey = key,
                ResourceFileName = $"badge-{key}.svg"
            });
        }
    }

    private IEnumerable<BaseItem> GetCollectionCandidateItems(BaseItem item)
    {
        var seen = new HashSet<Guid>();
        if (seen.Add(item.Id))
        {
            yield return item;
        }

        if (item is Episode episode)
        {
            var series = episode.Series ?? (episode.SeriesId == Guid.Empty ? null : _libraryManager.GetItemById(episode.SeriesId) as Series);
            if (series != null && seen.Add(series.Id))
            {
                yield return series;
            }
        }
        else if (item is Season season)
        {
            var series = season.Series ?? (season.SeriesId == Guid.Empty ? null : _libraryManager.GetItemById(season.SeriesId) as Series);
            if (series != null && seen.Add(series.Id))
            {
                yield return series;
            }
        }

        foreach (var parent in item.GetParents())
        {
            if (parent is Series or Season && seen.Add(parent.Id))
            {
                yield return parent;
            }
        }
    }

    private static bool CollectionContainsItem(BoxSet collection, BaseItem item)
    {
        var containsMethod = collection.GetType().GetMethod("ContainsLinkedChildByItemId", [typeof(Guid)]);
        if (containsMethod?.Invoke(collection, [item.Id]) is true)
        {
            return true;
        }

        try
        {
            if (collection.GetLinkedChildren().Any(child => ItemsMatch(child, item)))
            {
                return true;
            }
        }
        catch
        {
            // Fall through to linked child metadata checks.
        }

        try
        {
            if (collection.GetLinkedChildrenInfos().Any(info => LinkedChildMatches(info, item)))
            {
                return true;
            }
        }
        catch
        {
            // Some collection implementations may not have refreshed linked child metadata.
        }

        return false;
    }

    private static bool ItemsMatch(BaseItem candidate, BaseItem target)
    {
        if (candidate.Id == target.Id)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(candidate.Path) &&
            !string.IsNullOrWhiteSpace(target.Path) &&
            string.Equals(candidate.Path, target.Path, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var provider in target.ProviderIds)
        {
            if (candidate.ProviderIds.TryGetValue(provider.Key, out var value) &&
                string.Equals(value, provider.Value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LinkedChildMatches(object linkedChildInfo, BaseItem item)
    {
        if (linkedChildInfo is null)
        {
            return false;
        }

        var infoType = linkedChildInfo.GetType();
        var itemValue = infoType.GetProperty("Item2")?.GetValue(linkedChildInfo);
        if (itemValue is BaseItem linkedItem && ItemsMatch(linkedItem, item))
        {
            return true;
        }

        var linkedChildValue = infoType.GetProperty("Item1")?.GetValue(linkedChildInfo) ?? linkedChildInfo;
        var linkedChildType = linkedChildValue.GetType();

        if (GuidValueMatches(linkedChildType.GetProperty("ItemId")?.GetValue(linkedChildValue), item.Id) ||
            GuidValueMatches(linkedChildType.GetProperty("LibraryItemId")?.GetValue(linkedChildValue), item.Id))
        {
            return true;
        }

        var pathValue = linkedChildType.GetProperty("Path")?.GetValue(linkedChildValue) as string;
        return !string.IsNullOrWhiteSpace(pathValue) &&
            !string.IsNullOrWhiteSpace(item.Path) &&
            string.Equals(pathValue, item.Path, StringComparison.OrdinalIgnoreCase);
    }

    private static bool GuidValueMatches(object? value, Guid expected)
    {
        return value switch
        {
            Guid guid => guid == expected,
            string idString when Guid.TryParse(idString, out var parsed) => parsed == expected,
            _ => false
        };
    }

    private void DetectBadgesFromVideo(Video video, List<BadgeInfo> badges)
    {
        DetectBadgesFromVideo(video, badges, includeResolution: true);
    }

    private void DetectHdrAndAudioBadges(Video video, List<BadgeInfo> badges)
    {
        DetectHdrAndAudioBadges(video, badges, includeLanguages: true);
    }

    private void DetectHdrAndAudioBadges(Video video, List<BadgeInfo> badges, bool includeLanguages)
    {
        DetectBadgesFromVideo(video, badges, includeResolution: false, includeLanguages: includeLanguages);
    }

    private void DetectBadgesFromVideo(Video video, List<BadgeInfo> badges, bool includeResolution)
    {
        DetectBadgesFromVideo(video, badges, includeResolution, includeLanguages: true);
    }

    private void DetectBadgesFromVideo(Video video, List<BadgeInfo> badges, bool includeResolution, bool includeLanguages)
    {
        try
        {
            var mediaSources = video.GetMediaSources(false);
            var mediaSource = mediaSources?.FirstOrDefault();
            var videoStream = mediaSource?.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Video);

            if (videoStream != null)
            {
                if (includeResolution)
                {
                    var width = videoStream.Width ?? 0;
                    var height = videoStream.Height ?? 0;
                    var quality = DetermineQuality(width, height);
                    if (quality != VideoQuality.Unknown)
                    {
                        badges.Add(CreateResolutionBadge(quality));
                    }
                }

                // HDR detection - always detect, filtering happens in ShouldShowBadge
                var hdrBadge = DetectHdr(videoStream);
                if (hdrBadge != null)
                {
                    badges.Add(hdrBadge);
                }

                // Video codec detection
                var codec = videoStream.Codec?.ToLowerInvariant() ?? string.Empty;
                if (codec is "h264" or "avc")
                {
                    badges.Add(new BadgeInfo { Category = BadgeCategory.VideoCodec, BadgeKey = "h264", ResourceFileName = "badge-h264.svg" });
                }
                else if (codec is "hevc" or "h265")
                {
                    badges.Add(new BadgeInfo { Category = BadgeCategory.VideoCodec, BadgeKey = "hevc", ResourceFileName = "badge-hevc.svg" });
                }
                else if (codec == "av1")
                {
                    badges.Add(new BadgeInfo { Category = BadgeCategory.VideoCodec, BadgeKey = "av1", ResourceFileName = "badge-av1.svg" });
                }
                else if (codec == "vp9")
                {
                    badges.Add(new BadgeInfo { Category = BadgeCategory.VideoCodec, BadgeKey = "vp9", ResourceFileName = "badge-vp9.svg" });
                }
            }

            // 3D detection
            if (video.Video3DFormat.HasValue)
            {
                badges.Add(new BadgeInfo
                {
                    Category = BadgeCategory.ThreeD,
                    BadgeKey = "3d",
                    ResourceFileName = "badge-3d.svg"
                });
            }

            // Audio detection - prefer the default audio track
            var allAudioStreams = mediaSource?.MediaStreams?.Where(s => s.Type == MediaStreamType.Audio).ToList();
            if (allAudioStreams != null && allAudioStreams.Count > 0)
            {
                var defaultStream = allAudioStreams.FirstOrDefault(s => s.IsDefault);
                var streamsToAnalyze = defaultStream != null
                    ? new List<MediaStream> { defaultStream }
                    : new List<MediaStream> { allAudioStreams[0] };
                var audioBadges = DetectAudio(streamsToAnalyze);
                badges.AddRange(audioBadges);
            }

            if (includeLanguages)
            {
                AddLanguageBadgesFromMediaStreams(mediaSource?.MediaStreams, badges);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to detect badges for video: {ItemName}", video.Name);
        }
    }

    private static readonly Dictionary<string, string> LangCodeToFlag = new(StringComparer.OrdinalIgnoreCase)
    {
        { "en", "eng" }, { "english", "eng" },
        { "ja", "jpn" }, { "jp", "jpn" }, { "japanese", "jpn" },
        { "fr", "fra" }, { "fre", "fra" }, { "french", "fra" },
        { "de", "deu" }, { "ger", "deu" }, { "german", "deu" },
        { "nl", "nld" }, { "dut", "nld" }, { "dutch", "nld" },
        { "es", "spa" }, { "spanish", "spa" },
        { "it", "ita" }, { "italian", "ita" },
        { "pt", "por" }, { "portuguese", "por" },
        { "ko", "kor" }, { "korean", "kor" },
        { "zh", "zho" }, { "chi", "zho" }, { "chinese", "zho" },
        { "ru", "rus" }, { "russian", "rus" },
        { "ar", "ara" }, { "arabic", "ara" },
        { "hi", "hin" }, { "hindi", "hin" },
        { "th", "tha" }, { "thai", "tha" },
        { "pl", "pol" }, { "polish", "pol" },
        { "tr", "tur" }, { "turkish", "tur" },
        { "sv", "swe" }, { "swedish", "swe" },
        { "da", "dan" }, { "danish", "dan" },
        { "no", "nor" }, { "nb", "nor" }, { "nn", "nor" }, { "norwegian", "nor" },
        { "fi", "fin" }, { "finnish", "fin" },
        { "cs", "ces" }, { "cze", "ces" }, { "czech", "ces" },
        { "hu", "hun" }, { "hungarian", "hun" },
        { "ro", "ron" }, { "rum", "ron" }, { "romanian", "ron" },
        { "uk", "ukr" }, { "ukrainian", "ukr" },
        { "vi", "vie" }, { "vietnamese", "vie" },
        { "he", "heb" }, { "iw", "heb" }, { "hebrew", "heb" },
        { "el", "ell" }, { "gre", "ell" }, { "greek", "ell" },
        { "ms", "msa" }, { "may", "msa" }, { "malay", "msa" },
        { "tl", "fil" }, { "tgl", "fil" }, { "tagalog", "fil" }, { "filipino", "fil" },
        { "sk", "slk" }, { "slo", "slk" }, { "slovak", "slk" },
        { "eu", "eus" }, { "baq", "eus" }, { "basque", "eus" },
        { "cy", "cym" }, { "wel", "cym" }, { "welsh", "cym" },
        { "mandarin", "cmn" }, { "cantonese", "yue" }, { "taiwanese", "nan" },
        { "unknown", "und" }, { "undetermined", "und" }, { "undefined", "und" }
    };

    // Only include language codes that have a matching flag-{code}.svg asset.
    private static readonly HashSet<string> KnownFlagCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "fra", "eng", "jpn", "deu", "spa", "ita", "por", "kor", "zho", "rus",
        "nld", "ara", "hin", "tha", "pol", "tur", "swe", "dan", "nor", "fin",
        "ces", "hun", "ron", "ukr", "vie", "heb", "ell", "msa", "fil", "slk", "eus", "cym", "cmn", "yue", "nan", "und"
    };

    private static string NormalizeLanguageToken(string langCode)
    {
        var normalized = langCode.Trim().ToLowerInvariant();
        var firstToken = Regex.Split(normalized, @"[^a-z0-9]+").FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        return firstToken ?? normalized;
    }

    private static string? TryNormalizeFlagCode(string? langCode)
    {
        if (string.IsNullOrWhiteSpace(langCode))
        {
            return null;
        }

        var normalized = NormalizeLanguageToken(langCode);
        var mapped = LangCodeToFlag.TryGetValue(normalized, out var mappedCode) ? mappedCode : normalized;
        return KnownFlagCodes.Contains(mapped) ? mapped : null;
    }

    private static string GetFlagResourceFileName(string langCode)
    {
        var normalized = TryNormalizeFlagCode(langCode);
        if (string.IsNullOrEmpty(normalized)) return string.Empty;
        return string.Equals(normalized, "und", StringComparison.OrdinalIgnoreCase)
            ? "flag-und.svg"
            : $"flag-{normalized.ToLowerInvariant()}.svg";
    }

    private void DetectLanguageBadgesFromVideos(IEnumerable<Video> videos, List<BadgeInfo> badges)
    {
        var allStreams = new List<MediaStream>();
        foreach (var video in videos)
        {
            try
            {
                var mediaSource = video.GetMediaSources(false)?.FirstOrDefault();
                var streams = mediaSource?.MediaStreams;
                if (streams == null)
                {
                    continue;
                }

                allStreams.AddRange(streams.Where(s => s.Type is MediaStreamType.Audio or MediaStreamType.Subtitle));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to inspect language streams for child item: {ItemName}", video.Name);
            }
        }

        AddLanguageBadgesFromMediaStreams(allStreams, badges);
    }

    private static void AddLanguageBadgesFromMediaStreams(IEnumerable<MediaStream>? streams, List<BadgeInfo> badges)
    {
        var streamList = streams?.ToList() ?? [];
        var langBadges = DetectLanguages(streamList);
        badges.AddRange(langBadges);
    }

    /// <summary>
    /// Detects all language and subtitle badges. Always detects all languages;
    /// filtering by mode (DefaultOnly/All) is done in ShouldShowBadge.
    /// </summary>
    private static List<BadgeInfo> DetectLanguages(List<MediaStream> allStreams)
    {
        var badges = new List<BadgeInfo>();
        var audioStreams = allStreams.Where(s => s.Type == MediaStreamType.Audio).ToList();
        if (audioStreams.Count == 0)
        {
            badges.Add(CreateUndeterminedLanguageBadge());
            return badges;
        }

        var addedLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Detect all audio languages
        foreach (var stream in audioStreams)
        {
            var langLower = TryNormalizeFlagCode(stream.Language);
            if (!string.IsNullOrEmpty(langLower))
            {
                if (!addedLanguages.Add(langLower)) continue;
                badges.Add(new BadgeInfo
                {
                    Category = BadgeCategory.Language,
                    BadgeKey = langLower,
                    ResourceFileName = GetFlagResourceFileName(langLower)
                });
            }
        }

        if (badges.Count == 0)
        {
            badges.Add(CreateUndeterminedLanguageBadge());
        }

        // VOST indicators - always detect, filtering happens in ShouldShowBadge
        var audioLanguages = new HashSet<string>(
            audioStreams.Select(s => TryNormalizeFlagCode(s.Language)).Where(s => !string.IsNullOrEmpty(s))!,
            StringComparer.OrdinalIgnoreCase);

        var subtitleStreams = allStreams.Where(s => s.Type == MediaStreamType.Subtitle).ToList();
        foreach (var sub in subtitleStreams)
        {
            var subLang = TryNormalizeFlagCode(sub.Language);
            if (!string.IsNullOrEmpty(subLang) && !audioLanguages.Contains(subLang))
            {
                var key = "vost" + subLang;
                if (addedLanguages.Add(key))
                {
                    badges.Add(new BadgeInfo
                    {
                        Category = BadgeCategory.Subtitle,
                        BadgeKey = key,
                        ResourceFileName = string.Empty
                    });
                }
            }
        }

        return badges
            .OrderBy(b => b.Category == BadgeCategory.Subtitle ? 1 : 0)
            .ThenBy(b => b.BadgeKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static BadgeInfo CreateUndeterminedLanguageBadge() => new()
    {
        Category = BadgeCategory.Language,
        BadgeKey = "und",
        ResourceFileName = "flag-und.svg"
    };

    private static BadgeInfo? DetectHdr(MediaStream videoStream)
    {
        var rangeType = videoStream.VideoRangeType;

        // Dolby Vision variants (highest priority)
        if (rangeType is VideoRangeType.DOVI
            or VideoRangeType.DOVIWithHDR10
            or VideoRangeType.DOVIWithHLG
            or VideoRangeType.DOVIWithSDR
            or VideoRangeType.DOVIWithEL
            or VideoRangeType.DOVIWithHDR10Plus
            or VideoRangeType.DOVIWithELHDR10Plus)
        {
            return new BadgeInfo { Category = BadgeCategory.Hdr, BadgeKey = "dv", ResourceFileName = "badge-dv.svg" };
        }

        if (rangeType == VideoRangeType.HDR10Plus)
        {
            return new BadgeInfo { Category = BadgeCategory.Hdr, BadgeKey = "hdr10plus", ResourceFileName = "badge-hdr10plus.svg" };
        }

        if (rangeType == VideoRangeType.HLG)
        {
            return new BadgeInfo { Category = BadgeCategory.Hdr, BadgeKey = "hlg", ResourceFileName = "badge-hlg.svg" };
        }

        if (rangeType == VideoRangeType.HDR10)
        {
            return new BadgeInfo { Category = BadgeCategory.Hdr, BadgeKey = "hdr10", ResourceFileName = "badge-hdr10.svg" };
        }

        if (videoStream.VideoRange == VideoRange.HDR)
        {
            return new BadgeInfo { Category = BadgeCategory.Hdr, BadgeKey = "hdr", ResourceFileName = "badge-hdr.svg" };
        }

        return null;
    }

    private static List<BadgeInfo> DetectAudio(IEnumerable<MediaStream> audioStreams)
    {
        var badges = new List<BadgeInfo>();
        BadgeInfo? codecBadge = null;
        int codecPriority = -1;
        int bestChannels = 0;

        foreach (var stream in audioStreams)
        {
            var codec = stream.Codec?.ToUpperInvariant() ?? string.Empty;
            var profile = stream.Profile?.ToUpperInvariant() ?? string.Empty;
            var channels = stream.Channels ?? 0;

            if (channels > bestChannels) bestChannels = channels;

            int priority = -1;
            BadgeInfo? candidate = null;

            if (profile.Contains("ATMOS"))
            {
                priority = 7;
                candidate = new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "atmos", ResourceFileName = "badge-atmos.svg" };
            }
            else if (codec == "TRUEHD")
            {
                priority = 6;
                candidate = new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "truehd", ResourceFileName = "badge-truehd.svg" };
            }
            else if (profile.Contains("DTS:X") || profile.Contains("DTS-X") || profile.Contains("DTSX"))
            {
                priority = 5;
                candidate = new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "dtsx", ResourceFileName = "badge-dtsx.svg" };
            }
            else if (profile.Contains("DTS-HD MA") || profile.Contains("DTS-HD MASTER") || (codec == "DTS" && profile.Contains("MA")))
            {
                priority = 4;
                candidate = new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "dtshdma", ResourceFileName = "badge-dtshdma.svg" };
            }

            if (candidate != null && priority > codecPriority)
            {
                codecPriority = priority;
                codecBadge = candidate;
            }
        }

        if (codecBadge != null) badges.Add(codecBadge);

        if (bestChannels >= 8)
            badges.Add(new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "7.1", ResourceFileName = "badge-7_1.svg" });
        else if (bestChannels >= 6)
            badges.Add(new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "5.1", ResourceFileName = "badge-5_1.svg" });
        else if (bestChannels >= 2)
            badges.Add(new BadgeInfo { Category = BadgeCategory.Audio, BadgeKey = "stereo", ResourceFileName = "badge-stereo.svg" });

        return badges;
    }

    private static BadgeInfo CreateResolutionBadge(VideoQuality quality)
    {
        return quality switch
        {
            VideoQuality.UHD4K => new BadgeInfo { Category = BadgeCategory.Resolution, BadgeKey = "4k", ResourceFileName = "badge-4k.svg" },
            VideoQuality.FHD1080p => new BadgeInfo { Category = BadgeCategory.Resolution, BadgeKey = "1080p", ResourceFileName = "badge-1080p.svg" },
            VideoQuality.HD720p => new BadgeInfo { Category = BadgeCategory.Resolution, BadgeKey = "720p", ResourceFileName = "badge-720p.svg" },
            VideoQuality.SD => new BadgeInfo { Category = BadgeCategory.Resolution, BadgeKey = "sd", ResourceFileName = "badge-sd.svg" },
            _ => new BadgeInfo { Category = BadgeCategory.Resolution, BadgeKey = "unknown", ResourceFileName = string.Empty }
        };
    }

    private VideoQuality GetQualityFromVideo(Video video)
    {
        try
        {
            var mediaSources = video.GetMediaSources(false);
            var mediaSource = mediaSources?.FirstOrDefault();
            var videoStream = mediaSource?.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            if (videoStream == null) return VideoQuality.Unknown;

            var width = videoStream.Width ?? 0;
            var height = videoStream.Height ?? 0;
            return DetermineQuality(width, height);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get media sources for video item: {ItemName}", video.Name);
            return VideoQuality.Unknown;
        }
    }
}
