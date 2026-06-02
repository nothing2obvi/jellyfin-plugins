using System.Text.Json;
using System.Security.Claims;
using Jellyfin.Plugin.JellyTag.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTag.Services;

/// <summary>
/// Persists image variants learned from real client requests.
/// </summary>
public class LearnedClientProfileService : ILearnedClientProfileService
{
    private const string StateFileName = "learned-client-profile.json";
    private static readonly string[] DimensionQueryKeys = ["width", "height", "maxWidth", "maxHeight", "fillWidth", "fillHeight"];
    private static readonly string[] PreservedQueryKeys = ["quality"];
    private readonly ILogger<LearnedClientProfileService> _logger;
    private readonly object _lock = new();
    private readonly string _statePath;
    private LearnedClientProfileState? _state;
    private bool _loaded;

    /// <summary>
    /// Initializes a new instance of the <see cref="LearnedClientProfileService"/> class.
    /// </summary>
    public LearnedClientProfileService(ILogger<LearnedClientProfileService> logger)
    {
        _logger = logger;
        var cachePath = Plugin.Instance?.CacheFolderPath ?? Path.Combine(Path.GetTempPath(), "JellyTag", "cache");
        _statePath = Path.Combine(cachePath, StateFileName);
    }

    /// <inheritdoc />
    public void RecordVariant(BaseItem item, string imageType, IQueryCollection query, IHeaderDictionary headers, ClaimsPrincipal user)
    {
        if (!IsSupportedImageType(imageType))
        {
            return;
        }

        var normalizedQuery = NormalizeQuery(query);
        var phaseKey = GetPhaseKey(item);
        var key = CreateKey(imageType, phaseKey, normalizedQuery);
        var label = normalizedQuery.Count == 0
            ? "learned unsized"
            : "learned " + string.Join("&", normalizedQuery.Select(kvp => $"{kvp.Key}={kvp.Value}"));
        var source = GetRequestSource(headers, user);
        var nowTicks = DateTime.UtcNow.Ticks;

        lock (_lock)
        {
            var state = GetStateLocked();
            if (state.Variants.TryGetValue(key, out var existing))
            {
                existing.LastSeenUtcTicks = nowTicks;
                existing.FirstSeenUtcTicks = existing.FirstSeenUtcTicks == 0
                    ? (existing.LastSeenUtcTicks > 0 ? existing.LastSeenUtcTicks : nowTicks)
                    : existing.FirstSeenUtcTicks;
                existing.SeenCount = Math.Max(1, existing.SeenCount) + 1;
                ApplyBetterSource(existing, source);
                SaveLocked();
                return;
            }

            if (CacheWarmTask.IsFixedClientVariant(imageType, normalizedQuery))
            {
                return;
            }

            state.Variants[key] = new LearnedClientVariantEntry
            {
                ImageType = NormalizeImageType(imageType),
                PhaseKey = phaseKey,
                Label = label,
                Query = normalizedQuery,
                Client = source.Client,
                DeviceName = source.DeviceName,
                UserName = source.UserName,
                UserId = source.UserId,
                UserAgent = source.UserAgent,
                SeenCount = 1,
                FirstSeenUtcTicks = nowTicks,
                LastSeenUtcTicks = nowTicks
            };
            SaveLocked();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<LearnedClientVariant> GetVariants()
    {
        lock (_lock)
        {
            return GetStateLocked().Variants.Values
                .OrderBy(entry => entry.PhaseKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.ImageType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => CreateQueryKey(entry.Query), StringComparer.Ordinal)
                .Select(entry => new LearnedClientVariant(entry.ImageType, entry.PhaseKey, entry.Label, entry.Query))
                .ToList();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<LearnedClientVariantInfo> GetVariantInfo()
    {
        lock (_lock)
        {
            return GetStateLocked().Variants.Values
                .OrderBy(entry => entry.PhaseKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.ImageType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => CreateQueryKey(entry.Query), StringComparer.Ordinal)
                .Select(entry => new LearnedClientVariantInfo(
                    entry.ImageType,
                    entry.PhaseKey,
                    entry.Label,
                    entry.Query,
                    UnknownIfBlank(entry.Client),
                    UnknownIfBlank(entry.DeviceName),
                    UnknownIfBlank(entry.UserName),
                    UnknownIfBlank(entry.UserId),
                    UnknownIfBlank(entry.UserAgent),
                    Math.Max(0, entry.SeenCount),
                    TicksToUtc(entry.FirstSeenUtcTicks),
                    TicksToUtc(entry.LastSeenUtcTicks)))
                .ToList();
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_lock)
        {
            _state = new LearnedClientProfileState();
            _loaded = true;
            TryDelete(_statePath);
            foreach (var tempFile in GetTemporaryStateFiles())
            {
                TryDelete(tempFile);
            }
        }
    }

    private LearnedClientProfileState GetStateLocked()
    {
        if (_loaded)
        {
            return _state ??= new LearnedClientProfileState();
        }

        _loaded = true;
        foreach (var tempFile in GetTemporaryStateFiles())
        {
            TryDelete(tempFile);
        }

        if (!File.Exists(_statePath))
        {
            _state = new LearnedClientProfileState();
            return _state;
        }

        try
        {
            _state = JsonSerializer.Deserialize<LearnedClientProfileState>(File.ReadAllText(_statePath)) ?? new LearnedClientProfileState();
            _state.Variants ??= new Dictionary<string, LearnedClientVariantEntry>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read JellyTag-Plus learned client profile; starting fresh");
            _state = new LearnedClientProfileState();
        }

        return _state;
    }

    private void SaveLocked()
    {
        var directory = Path.GetDirectoryName(_statePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{_statePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(GetStateLocked(), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tempPath, _statePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write JellyTag-Plus learned client profile");
            TryDelete(tempPath);
        }
    }

    private IEnumerable<string> GetTemporaryStateFiles()
    {
        var directory = Path.GetDirectoryName(_statePath);
        return string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)
            ? []
            : Directory.GetFiles(directory, $"{Path.GetFileName(_statePath)}.*.tmp");
    }

    private static bool IsSupportedImageType(string imageType)
    {
        return string.Equals(imageType, "Primary", StringComparison.OrdinalIgnoreCase)
            || string.Equals(imageType, "Thumb", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeImageType(string imageType)
    {
        return string.Equals(imageType, "Thumb", StringComparison.OrdinalIgnoreCase) ? "Thumb" : "Primary";
    }

    private static string GetPhaseKey(BaseItem item)
    {
        if (item is Episode)
        {
            return CacheWarmTask.EpisodesPhaseKey;
        }

        if (item is MusicVideo || item.GetType() == typeof(Video))
        {
            return CacheWarmTask.VideosPhaseKey;
        }

        return CacheWarmTask.HomeLibrariesPhaseKey;
    }

    private static List<KeyValuePair<string, string>> NormalizeQuery(IQueryCollection query)
    {
        var normalized = new List<KeyValuePair<string, string>>();
        foreach (var key in DimensionQueryKeys)
        {
            if (query.TryGetValue(key, out var values) && int.TryParse(values.FirstOrDefault(), out var value))
            {
                normalized.Add(new KeyValuePair<string, string>(key, RoundToNearestTen(value).ToString()));
            }
        }

        foreach (var key in PreservedQueryKeys)
        {
            if (query.TryGetValue(key, out var values) && int.TryParse(values.FirstOrDefault(), out var value))
            {
                normalized.Add(new KeyValuePair<string, string>(key, value.ToString()));
            }
        }

        return normalized
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int RoundToNearestTen(int value)
    {
        return Math.Max(1, (int)(Math.Round(value / 10.0, MidpointRounding.AwayFromZero) * 10));
    }

    private static RequestSource GetRequestSource(IHeaderDictionary headers, ClaimsPrincipal user)
    {
        var authorization = ParseEmbyAuthorization(FirstNonBlank(
            GetHeader(headers, "X-Emby-Authorization"),
            GetHeader(headers, "Authorization")));
        var client = FirstNonBlank(
            GetHeader(headers, "X-Emby-Client"),
            GetHeader(headers, "X-MediaBrowser-Client"),
            GetHeader(headers, "X-Jellyfin-Client"),
            GetHeader(headers, "X-Client"),
            GetHeader(headers, "X-Application"),
            GetAuthValue(authorization, "Client"));
        var deviceName = FirstNonBlank(
            GetHeader(headers, "X-Emby-Device-Name"),
            GetHeader(headers, "X-MediaBrowser-Device-Name"),
            GetHeader(headers, "X-Jellyfin-Device-Name"),
            GetAuthValue(authorization, "Device"));
        var userName = FirstNonBlank(
            user.Identity?.Name,
            GetClaim(user, ClaimTypes.Name),
            GetClaim(user, "name"),
            GetClaim(user, "preferred_username"));
        var userId = FirstNonBlank(
            GetClaim(user, ClaimTypes.NameIdentifier),
            GetClaim(user, "sub"),
            GetClaim(user, "user_id"),
            GetAuthValue(authorization, "UserId"));

        return new RequestSource(
            client,
            deviceName,
            userName,
            userId,
            GetHeader(headers, "User-Agent"));
    }

    private static void ApplyBetterSource(LearnedClientVariantEntry entry, RequestSource source)
    {
        entry.Client = PreferKnown(entry.Client, source.Client);
        entry.DeviceName = PreferKnown(entry.DeviceName, source.DeviceName);
        entry.UserName = PreferKnown(entry.UserName, source.UserName);
        entry.UserId = PreferKnown(entry.UserId, source.UserId);
        entry.UserAgent = PreferKnown(entry.UserAgent, source.UserAgent);
    }

    private static string PreferKnown(string current, string replacement)
    {
        return IsKnown(current) ? current : replacement;
    }

    private static bool IsKnown(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && !string.Equals(value, "Unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static string UnknownIfBlank(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
    }

    private static DateTime? TicksToUtc(long ticks)
    {
        return ticks <= 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
    }

    private static string GetHeader(IHeaderDictionary headers, string key)
    {
        return headers.TryGetValue(key, out var values) ? values.FirstOrDefault() ?? string.Empty : string.Empty;
    }

    private static string? GetClaim(ClaimsPrincipal user, string type)
    {
        return user.Claims.FirstOrDefault(claim => string.Equals(claim.Type, type, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    private static string FirstNonBlank(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static string GetAuthValue(Dictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) ? value : string.Empty;
    }

    private static Dictionary<string, string> ParseEmbyAuthorization(string header)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(header))
        {
            return values;
        }

        foreach (var part in header.Split(','))
        {
            var trimmed = part.Trim();
            var separator = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = trimmed[..separator].Trim();
            const string mediaBrowserPrefix = "MediaBrowser ";
            if (key.StartsWith(mediaBrowserPrefix, StringComparison.OrdinalIgnoreCase))
            {
                key = key[mediaBrowserPrefix.Length..].Trim();
            }
            var value = trimmed[(separator + 1)..].Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                values[key] = value;
            }
        }

        return values;
    }

    private static string CreateKey(string imageType, string phaseKey, IReadOnlyList<KeyValuePair<string, string>> query)
    {
        return $"{NormalizeImageType(imageType)}:{phaseKey}:{CreateQueryKey(query)}";
    }

    private static string CreateQueryKey(IReadOnlyList<KeyValuePair<string, string>> query)
    {
        return string.Join("&", query
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => $"{kvp.Key}={kvp.Value}"));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed class LearnedClientProfileState
    {
        public int Version { get; set; } = 1;

        public Dictionary<string, LearnedClientVariantEntry> Variants { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class LearnedClientVariantEntry
    {
        public string ImageType { get; set; } = "Primary";

        public string PhaseKey { get; set; } = CacheWarmTask.HomeLibrariesPhaseKey;

        public string Label { get; set; } = string.Empty;

        public List<KeyValuePair<string, string>> Query { get; set; } = new();

        public string Client { get; set; } = string.Empty;

        public string DeviceName { get; set; } = string.Empty;

        public string UserName { get; set; } = string.Empty;

        public string UserId { get; set; } = string.Empty;

        public string UserAgent { get; set; } = string.Empty;

        public int SeenCount { get; set; }

        public long FirstSeenUtcTicks { get; set; }

        public long LastSeenUtcTicks { get; set; }
    }

    private sealed record RequestSource(string Client, string DeviceName, string UserName, string UserId, string UserAgent);
}
