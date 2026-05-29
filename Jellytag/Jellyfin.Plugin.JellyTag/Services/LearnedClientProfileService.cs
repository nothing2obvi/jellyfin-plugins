using System.Text.Json;
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
    public void RecordVariant(BaseItem item, string imageType, IQueryCollection query)
    {
        if (!IsSupportedImageType(imageType))
        {
            return;
        }

        var normalizedQuery = NormalizeQuery(query);
        if (CacheWarmTask.IsFixedClientVariant(imageType, normalizedQuery))
        {
            return;
        }

        var phaseKey = GetPhaseKey(item);
        var key = CreateKey(imageType, phaseKey, normalizedQuery);
        var label = normalizedQuery.Count == 0
            ? "learned unsized"
            : "learned " + string.Join("&", normalizedQuery.Select(kvp => $"{kvp.Key}={kvp.Value}"));

        lock (_lock)
        {
            var state = GetStateLocked();
            if (state.Variants.ContainsKey(key))
            {
                state.Variants[key].LastSeenUtcTicks = DateTime.UtcNow.Ticks;
                SaveLocked();
                return;
            }

            state.Variants[key] = new LearnedClientVariantEntry
            {
                ImageType = NormalizeImageType(imageType),
                PhaseKey = phaseKey,
                Label = label,
                Query = normalizedQuery,
                LastSeenUtcTicks = DateTime.UtcNow.Ticks
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

        public long LastSeenUtcTicks { get; set; }
    }
}
