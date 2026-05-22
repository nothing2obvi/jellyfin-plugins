using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyTag.Configuration;
using Jellyfin.Plugin.JellyTag.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTag.Tasks;

public class CacheWarmTask : IScheduledTask
{
    private static int _isRunning;
    private static readonly IReadOnlyList<WarmupPhase> WarmupPhases =
    [
        new WarmupPhase("home", "Home", 0),
        new WarmupPhase("libraries", "Libraries", 1),
        new WarmupPhase("episodes", "Episodes", 2),
        new WarmupPhase("other", "Other", 3)
    ];

    private static readonly string[] DefaultClientWarmupProfileKeys = ["androidtv", "roku", "streamyfin", "wholphin", "findroid"];
    private static readonly IReadOnlyList<ClientWarmupProfile> ClientWarmupProfiles =
    [
        CreateFindroidProfile(),
        // Jellyfin Web and WebShellClients are intentionally not warmed here. WebShellClients means
        // Android/iOS/Desktop Qt using Jellyfin Web inside the native app shell; those clients compute
        // image sizes dynamically, so we avoid hardcoded guessed presets.
        CreateAndroidTvProfile(),
        CreateRokuProfile(),
        CreateStreamyfinProfile(),
        CreateWholphinProfile()
    ];

    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationHost _applicationHost;
    private readonly IImageTrafficCoordinator _trafficCoordinator;
    private readonly ILogger<CacheWarmTask> _logger;

    public CacheWarmTask(ILibraryManager libraryManager, IServerApplicationHost applicationHost, IImageTrafficCoordinator trafficCoordinator, ILogger<CacheWarmTask> logger)
    {
        _libraryManager = libraryManager;
        _applicationHost = applicationHost;
        _trafficCoordinator = trafficCoordinator;
        _logger = logger;
    }

    public string Name => "JellyTag-Plus Cache Warmer";
    public string Key => "JellyTagPlusCacheWarmer";
    public string Description => "Pre-renders JellyTag-Plus poster and thumbnail overlays for enabled libraries.";
    public string Category => "JellyTag-Plus";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            _logger.LogInformation("JellyTag-Plus cache warmer is already running; skipping overlapping scheduled task run");
            progress.Report(100);
            return;
        }

        using var runLease = new WarmupRunLease();

        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.Enabled) { progress.Report(100); return; }

        var state = WarmupStateStore.Load(CreateWarmupScope(config), _logger);
        var warmerStateMaxAgeHours = GetWarmerStateMaxAgeHours(config);
        state.PruneExpired(warmerStateMaxAgeHours);
        var profiles = GetEnabledClientWarmupProfiles(config).ToList();
        if (profiles.Count == 0) { progress.Report(100); return; }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode, BaseItemKind.Video]
        }).Where(item => IsInEnabledLibrary(item, config)).ToList();

        var requests = new List<WarmupRequest>();
        for (var profileIndex = 0; profileIndex < profiles.Count; profileIndex++)
        {
            var profile = profiles[profileIndex];
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ShouldRequestPrimary(item, config) && item.HasImage(ImageType.Primary, 0))
                {
                    requests.AddRange(CreateWarmupRequests(profile, profileIndex, item, "Primary"));
                }

                if (ShouldRequestThumb(item, config) && item.HasImage(ImageType.Thumb, 0))
                {
                    requests.AddRange(CreateWarmupRequests(profile, profileIndex, item, "Thumb"));
                }
            }
        }

        requests = requests
            .GroupBy(request => request.CacheKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(request => request.Phase.Order)
            .ThenBy(request => request.ClientProfileOrder)
            .ThenBy(request => request.ItemId)
            .ThenBy(request => request.ImageType, StringComparer.Ordinal)
            .ThenBy(request => request.Variant.CacheKey, StringComparer.Ordinal)
            .ToList();
        var deduplicatedRequests = requests.Count;
        requests = requests
            .Where(request => !state.Contains(request.CompletionKey, warmerStateMaxAgeHours))
            .ToList();
        var skipped = deduplicatedRequests - requests.Count;

        if (requests.Count == 0) { progress.Report(100); return; }

        var baseUrl = GetBaseUrl();
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };

        var completed = 0;
        var warmed = 0;
        var failed = 0;
        var maxConcurrency = Math.Clamp(config.WarmerMaxConcurrency <= 0 ? 1 : config.WarmerMaxConcurrency, 1, 8);
        var delayMs = Math.Clamp(config.WarmerDelayMs, 0, 10000);
        var quietPeriod = TimeSpan.FromSeconds(Math.Clamp(config.WarmerClientQuietSeconds, 0, 120));
        using var throttler = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        foreach (var phase in WarmupPhases)
        {
            var phaseRequests = requests.Where(request => request.Phase.Key == phase.Key).ToList();
            if (phaseRequests.Count == 0)
            {
                continue;
            }

            _logger.LogInformation("JellyTag-Plus cache warmer starting {Phase} phase with {Count} requests", phase.Name, phaseRequests.Count);
            var tasks = phaseRequests.Select(async request =>
            {
                await throttler.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await _trafficCoordinator.WaitForClientQuietPeriodAsync(quietPeriod, cancellationToken).ConfigureAwait(false);
                    var url = request.ToUrl(baseUrl);
                    using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        state.MarkCompleted(request.CompletionKey);
                        Interlocked.Increment(ref warmed);
                    }
                    else
                    {
                        Interlocked.Increment(ref failed);
                        _logger.LogDebug("JellyTag-Plus cache warmer got {StatusCode} for {Url}", response.StatusCode, url);
                    }

                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    _logger.LogDebug(ex, "JellyTag-Plus cache warmer failed for {ItemId} {ImageType} using {ClientProfile}", request.ItemId, request.ImageType, request.ClientProfile);
                }
                finally
                {
                    var done = Interlocked.Increment(ref completed);
                    progress.Report(done * 100.0 / requests.Count);
                    throttler.Release();
                }
            }).ToArray();

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        _logger.LogInformation("JellyTag-Plus cache warmer complete. Requested {Total}, newly warmed {Warmed}, failed {Failed}, skipped already warmed {Skipped}", requests.Count, warmed, failed, skipped);
    }

    private sealed class WarmupRunLease : IDisposable
    {
        public void Dispose()
        {
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }

    public static IReadOnlyList<WarmerClientProgress> GetEstimatedClientProgress(PluginConfiguration config, ILibraryManager libraryManager, ILogger<CacheWarmTask> logger)
    {
        var state = WarmupStateStore.Load(CreateWarmupScope(config), logger);
        var warmerStateMaxAgeHours = GetWarmerStateMaxAgeHours(config);
        state.PruneExpired(warmerStateMaxAgeHours);

        var items = libraryManager.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode, BaseItemKind.Video]
        }).Where(item => IsInEnabledLibrary(item, config, libraryManager)).ToList();

        var enabledKeys = GetEnabledClientProfileKeys(config).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return GetOrderedClientWarmupProfiles(config, includeDisabled: true)
            .Select(profile =>
            {
                var requests = new List<WarmupRequest>();
                foreach (var item in items)
                {
                    if (ShouldRequestPrimary(item, config) && item.HasImage(ImageType.Primary, 0))
                    {
                        requests.AddRange(CreateWarmupRequests(profile, 0, item, "Primary"));
                    }

                    if (ShouldRequestThumb(item, config) && item.HasImage(ImageType.Thumb, 0))
                    {
                        requests.AddRange(CreateWarmupRequests(profile, 0, item, "Thumb"));
                    }
                }

                requests = requests
                    .GroupBy(request => request.CacheKey, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToList();

                var completed = requests.Count(request => state.Contains(request.CompletionKey, warmerStateMaxAgeHours));
                var total = requests.Count;
                var percent = total == 0 ? 100 : Math.Round(completed * 100.0 / total, 1);
                var phases = WarmupPhases
                    .Select(phase =>
                    {
                        var phaseRequests = requests.Where(request => request.Phase.Key == phase.Key).ToList();
                        var phaseCompleted = phaseRequests.Count(request => state.Contains(request.CompletionKey, warmerStateMaxAgeHours));
                        var phaseTotal = phaseRequests.Count;
                        var phasePercent = phaseTotal == 0 ? 100 : Math.Round(phaseCompleted * 100.0 / phaseTotal, 1);
                        return new WarmerPhaseProgress(phase.Key, phase.Name, phaseCompleted, phaseTotal, phasePercent);
                    })
                    .ToList();

                return new WarmerClientProgress(profile.Key, profile.Name, enabledKeys.Contains(profile.Key), completed, total, percent, phases);
            })
            .ToList();
    }

    private static IEnumerable<ClientWarmupProfile> GetEnabledClientWarmupProfiles(PluginConfiguration config)
    {
        return GetOrderedClientWarmupProfiles(config, includeDisabled: false);
    }

    private static IEnumerable<ClientWarmupProfile> GetOrderedClientWarmupProfiles(PluginConfiguration config, bool includeDisabled)
    {
        var profileMap = ClientWarmupProfiles.ToDictionary(profile => profile.Key, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enabledKeys = GetEnabledClientProfileKeys(config).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var key in GetConfiguredClientProfileOrder(config))
        {
            var normalizedKey = key.Trim();
            if (string.IsNullOrWhiteSpace(normalizedKey) || !seen.Add(normalizedKey))
            {
                continue;
            }

            if (profileMap.TryGetValue(normalizedKey, out var profile))
            {
                if (includeDisabled || enabledKeys.Contains(profile.Key))
                {
                    yield return profile;
                }
            }
        }

        if (!includeDisabled)
        {
            yield break;
        }

        foreach (var profile in ClientWarmupProfiles)
        {
            if (seen.Add(profile.Key))
            {
                yield return profile;
            }
        }
    }

    private static IEnumerable<string> GetEnabledClientProfileKeys(PluginConfiguration config)
    {
        return config.WarmerClientProfiles ?? DefaultClientWarmupProfileKeys.AsEnumerable();
    }

    private static IEnumerable<string> GetConfiguredClientProfileOrder(PluginConfiguration config)
    {
        return config.WarmerClientProfileOrder?.Count > 0
            ? config.WarmerClientProfileOrder
            : GetEnabledClientProfileKeys(config).Concat(DefaultClientWarmupProfileKeys);
    }

    private static int? GetWarmerStateMaxAgeHours(PluginConfiguration config)
    {
        return config.CacheDurationHours <= 0 ? null : config.CacheDurationHours;
    }

    private static IEnumerable<WarmupRequest> CreateWarmupRequests(ClientWarmupProfile profile, int profileOrder, BaseItem item, string imageType)
    {
        var imageVersion = GetImageVersion(item, imageType);
        var itemModifiedTicks = item.DateModified.Ticks;
        foreach (var variant in profile.GetVariants(imageType))
        {
            var phase = GetRequestPhase(item, variant);
            yield return new WarmupRequest(item.Id, imageType, imageVersion, itemModifiedTicks, profile.Name, profileOrder, phase, variant);
        }
    }

    private static WarmupPhase GetRequestPhase(BaseItem item, ImageVariant variant)
    {
        return item is Episode ? WarmupPhases[2] : variant.Phase;
    }

    private string GetBaseUrl()
    {
        var scheme = _applicationHost.ListenWithHttps ? "https" : "http";
        var port = _applicationHost.ListenWithHttps ? _applicationHost.HttpsPort : _applicationHost.HttpPort;
        var url = _applicationHost.GetLocalApiUrl("127.0.0.1", scheme, port);
        return string.IsNullOrWhiteSpace(url) ? $"{scheme}://127.0.0.1:{port}" : url.TrimEnd('/');
    }

    private bool IsInEnabledLibrary(BaseItem item, PluginConfiguration config)
    {
        return IsInEnabledLibrary(item, config, _libraryManager);
    }

    private static bool IsInEnabledLibrary(BaseItem item, PluginConfiguration config, ILibraryManager libraryManager)
    {
        var folders = libraryManager.GetCollectionFolders(item).ToList();
        if (folders.Count == 0) return true;
        if (config.ExcludedLibraryIds?.Count > 0 && folders.Any(f => config.ExcludedLibraryIds.Contains(f.Id.ToString("N")))) return false;
        return true;
    }

    private static bool ShouldRequestPrimary(BaseItem item, PluginConfiguration config)
    {
        if (item is Episode) return config.ThumbnailSameAsPoster ? config.PosterConfig.Enabled : config.ThumbnailConfig.Enabled;
        return config.PosterConfig.Enabled;
    }

    private static bool ShouldRequestThumb(BaseItem item, PluginConfiguration config)
    {
        return config.ThumbnailSameAsPoster ? config.PosterConfig.Enabled : config.ThumbnailConfig.Enabled;
    }

    private static ClientWarmupProfile CreateFindroidProfile()
    {
        return new ClientWarmupProfile(
            "findroid",
            "Findroid",
            "Findroid home and library views request unsized Primary images and let the client scale them.",
            [ImageVariant.Unsized(WarmupPhases[0], "home/library unsized")],
            []);
    }

    private static ClientWarmupProfile CreateAndroidTvProfile()
    {
        var primaryVariants = new[]
        {
            ImageVariant.MaxSize(WarmupPhases[0], 100, 150, "home poster"),
            ImageVariant.MaxSize(WarmupPhases[1], 116, 174, "library poster smallest"),
            ImageVariant.MaxSize(WarmupPhases[1], 118, 178, "library poster vertical smallest"),
            ImageVariant.MaxSize(WarmupPhases[1], 136, 205, "library poster vertical small"),
            ImageVariant.MaxSize(WarmupPhases[1], 144, 217, "library poster small"),
            ImageVariant.MaxSize(WarmupPhases[1], 161, 242, "library poster vertical medium"),
            ImageVariant.MaxSize(WarmupPhases[1], 192, 288, "library poster medium"),
            ImageVariant.MaxSize(WarmupPhases[1], 252, 379, "library poster vertical large"),
            ImageVariant.MaxSize(WarmupPhases[1], 284, 427, "library poster large"),
            ImageVariant.MaxSize(WarmupPhases[1], 352, 528, "library poster x-large")
        };

        var thumbVariants = new[]
        {
            ImageVariant.MaxSize(WarmupPhases[0], 267, 150, "home thumb/backdrop"),
            ImageVariant.MaxSize(WarmupPhases[1], 161, 91, "library thumb vertical smallest"),
            ImageVariant.MaxSize(WarmupPhases[1], 197, 111, "library thumb vertical small"),
            ImageVariant.MaxSize(WarmupPhases[1], 222, 125, "library thumb smallest"),
            ImageVariant.MaxSize(WarmupPhases[1], 252, 142, "library thumb vertical medium"),
            ImageVariant.MaxSize(WarmupPhases[1], 259, 146, "library thumb small"),
            ImageVariant.MaxSize(WarmupPhases[1], 309, 174, "library thumb medium"),
            ImageVariant.MaxSize(WarmupPhases[1], 352, 198, "library thumb vertical large"),
            ImageVariant.MaxSize(WarmupPhases[1], 385, 217, "library thumb large"),
            ImageVariant.MaxSize(WarmupPhases[1], 581, 327, "library thumb vertical x-large"),
            ImageVariant.MaxSize(WarmupPhases[1], 759, 427, "library thumb x-large")
        };

        return new ClientWarmupProfile("androidtv", "Android TV", "maxWidth/maxHeight requests for home rows and poster-size library settings.", primaryVariants, thumbVariants);
    }

    private static ClientWarmupProfile CreateRokuProfile()
    {
        var primaryVariants = new[]
        {
            ImageVariant.MaxSize(WarmupPhases[1], 196, 384, "default poster helper", 90),
            ImageVariant.MaxSize(WarmupPhases[0], 180, 331, "home movie poster display", 90),
            ImageVariant.MaxSize(WarmupPhases[1], 295, 440, "library poster data", 90),
            ImageVariant.MaxSize(WarmupPhases[1], 300, 450, "person/search style poster", 90),
            ImageVariant.MaxSize(WarmupPhases[0], 464, 331, "home/library wide poster", 90),
            ImageVariant.MaxSize(WarmupPhases[2], 400, 384, "episode row actual fallback", 90),
            ImageVariant.MaxSize(WarmupPhases[1], 500, 500, "square audio/library art", 90)
        };

        var thumbVariants = new[]
        {
            ImageVariant.MaxSize(WarmupPhases[1], 196, 384, "default thumb fallback", 90),
            ImageVariant.MaxSize(WarmupPhases[1], 295, 440, "portrait fallback thumb", 90),
            ImageVariant.MaxSize(WarmupPhases[2], 400, 384, "episode row actual fallback", 90),
            ImageVariant.MaxSize(WarmupPhases[0], 464, 331, "series/home landscape thumb", 90),
            ImageVariant.MaxSize(WarmupPhases[1], 500, 500, "square thumb art", 90)
        };

        return new ClientWarmupProfile("roku", "Roku", "Mostly fixed maxWidth/maxHeight requests used by Roku data nodes and home rows.", primaryVariants, thumbVariants);
    }

    private static ClientWarmupProfile CreateStreamyfinProfile()
    {
        var primaryVariants = new[]
        {
            ImageVariant.Width(WarmupPhases[1], 1000, 90, "main library ItemImage"),
            ImageVariant.FillWidth(WarmupPhases[0], 300, 80, "poster rows"),
            ImageVariant.FillHeight(WarmupPhases[2], 389, 80, "episode poster / horizontal fallback")
        };

        var thumbVariants = new[]
        {
            ImageVariant.FillHeight(WarmupPhases[0], 389, 80, "continue watching / next up horizontal cards")
        };

        return new ClientWarmupProfile("streamyfin", "Streamyfin", "width/fillWidth/fillHeight requests used in library and home poster components.", primaryVariants, thumbVariants);
    }

    private static ClientWarmupProfile CreateWholphinProfile()
    {
        var variants = new[]
        {
            ImageVariant.QualityOnly(WarmupPhases[1], 96, "detail and provider image default"),
            ImageVariant.FillHeight(WarmupPhases[0], 172, 96, "default home row height"),
            ImageVariant.FillHeight(WarmupPhases[0], 148, 96, "compact home row height"),
            ImageVariant.FillHeight(WarmupPhases[0], 128, 96, "episode, genre, and square row height"),
            ImageVariant.FillHeight(WarmupPhases[0], 100, 96, "compact episode and wide row height"),
            ImageVariant.FillHeight(WarmupPhases[0], 96, 96, "Live TV row height")
        };

        return new ClientWarmupProfile("wholphin", "Wholphin", "quality=96 and fixed fillHeight requests used by Wholphin rows and cards; dynamic grid fillWidth values are intentionally not guessed.", variants, variants);
    }

    private sealed record ClientWarmupProfile(
        string Key,
        string Name,
        string Notes,
        IReadOnlyList<ImageVariant> PrimaryVariants,
        IReadOnlyList<ImageVariant> ThumbVariants)
    {
        public IReadOnlyList<ImageVariant> GetVariants(string imageType)
        {
            return string.Equals(imageType, "Thumb", StringComparison.OrdinalIgnoreCase) ? ThumbVariants : PrimaryVariants;
        }
    }

    private sealed record WarmupPhase(string Key, string Name, int Order);

    private sealed record ImageVariant(WarmupPhase Phase, string Label, IReadOnlyList<KeyValuePair<string, string>> Query)
    {
        public string CacheKey => string.Join("&", Query.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase).Select(kvp => $"{kvp.Key}={kvp.Value}"));

        public static ImageVariant Unsized(WarmupPhase phase, string label)
        {
            return new ImageVariant(phase, label, []);
        }

        public static ImageVariant MaxSize(WarmupPhase phase, int width, int height, string label, int? quality = null)
        {
            return Create(phase, label, quality, ("maxWidth", width), ("maxHeight", height));
        }

        public static ImageVariant FillWidth(WarmupPhase phase, int width, int quality, string label)
        {
            return Create(phase, label, quality, ("fillWidth", width));
        }

        public static ImageVariant FillHeight(WarmupPhase phase, int height, int quality, string label)
        {
            return Create(phase, label, quality, ("fillHeight", height));
        }

        public static ImageVariant QualityOnly(WarmupPhase phase, int quality, string label)
        {
            return Create(phase, label, quality);
        }

        public static ImageVariant Width(WarmupPhase phase, int width, int quality, string label)
        {
            return Create(phase, label, quality, ("width", width));
        }

        private static ImageVariant Create(WarmupPhase phase, string label, int? quality, params (string Key, int Value)[] dimensions)
        {
            var query = dimensions
                .Select(dimension => new KeyValuePair<string, string>(dimension.Key, dimension.Value.ToString()))
                .ToList();

            if (quality.HasValue)
            {
                query.Add(new KeyValuePair<string, string>("quality", quality.Value.ToString()));
            }

            return new ImageVariant(phase, label, query);
        }
    }

    private static string GetImageVersion(BaseItem item, string imageType)
    {
        var parsedImageType = string.Equals(imageType, "Thumb", StringComparison.OrdinalIgnoreCase) ? ImageType.Thumb : ImageType.Primary;
        try
        {
            var imageInfo = item.GetImageInfo(parsedImageType, 0);
            var imagePath = item.GetImagePath(parsedImageType, 0);
            return imageInfo?.DateModified.Ticks.ToString() ?? (imagePath == null ? "unknown" : ComputeShortHash(imagePath));
        }
        catch
        {
            return "unknown";
        }
    }

    private static string CreateWarmupScope(PluginConfiguration config)
    {
        var scope = new
        {
            Version = "warmer-v3",
            config.Enabled,
            config.ThumbnailSameAsPoster,
            config.ThumbnailSizeReduction,
            config.ExcludedLibraryIds,
            config.LibraryBadgeOptions,
            config.CustomBadgeTexts,
            config.PosterConfig,
            config.ThumbnailConfig,
            config.OutputFormat,
            config.JpegQuality,
            config.WebPQuality
        };
        var json = JsonSerializer.Serialize(scope);
        var input = $"warmer-v3|{json}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..16];
    }

    private static string ComputeShortHash(string input)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..16];
    }

    private sealed record WarmupRequest(Guid ItemId, string ImageType, string ImageVersion, long ItemModifiedTicks, string ClientProfile, int ClientProfileOrder, WarmupPhase Phase, ImageVariant Variant)
    {
        public string CacheKey => $"{ItemId:N}:{ImageType}:{Variant.CacheKey}";
        public string CompletionKey => $"{ItemId:N}:{ImageType}:{ImageVersion}:{ItemModifiedTicks}:{Variant.CacheKey}";

        public string ToUrl(string baseUrl)
        {
            var query = new List<KeyValuePair<string, string>>(Variant.Query)
            {
                new("jellytagwarm", "1")
            };

            var queryString = string.Join("&", query.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
            return $"{baseUrl}/Items/{ItemId:N}/Images/{ImageType}?{queryString}";
        }
    }

    private sealed class WarmupState
    {
        public int Version { get; set; } = 1;
        public string Scope { get; set; } = string.Empty;
        public Dictionary<string, long> CompletedKeys { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class WarmupStateStore
    {
        private const string StateFileName = "cache-warmer-state.json";
        private readonly string _statePath;
        private readonly ILogger<CacheWarmTask> _logger;
        private readonly object _lock = new();
        private readonly WarmupState _state;

        private WarmupStateStore(string scope, string statePath, WarmupState state, ILogger<CacheWarmTask> logger)
        {
            _statePath = statePath;
            _logger = logger;
            _state = state.Scope == scope ? state : new WarmupState { Scope = scope };
            _state.CompletedKeys = new Dictionary<string, long>(_state.CompletedKeys ?? [], StringComparer.Ordinal);
        }

        public static WarmupStateStore Load(string scope, ILogger<CacheWarmTask> logger)
        {
            var statePath = GetStatePath();
            CleanTemporaryStateFiles(statePath, logger);

            try
            {
                if (File.Exists(statePath))
                {
                    var json = File.ReadAllText(statePath);
                    var state = JsonSerializer.Deserialize<WarmupState>(json) ?? new WarmupState { Scope = scope };
                    return new WarmupStateStore(scope, statePath, state, logger);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read JellyTag-Plus cache warmer state; starting a fresh warmup ledger");
            }

            return new WarmupStateStore(scope, statePath, new WarmupState { Scope = scope }, logger);
        }

        public bool Contains(string key, int? maxAgeHours)
        {
            lock (_lock)
            {
                if (!_state.CompletedKeys.TryGetValue(key, out var completedTicks))
                {
                    return false;
                }

                if (!maxAgeHours.HasValue)
                {
                    return true;
                }

                var completedUtc = new DateTime(completedTicks, DateTimeKind.Utc);
                if ((DateTime.UtcNow - completedUtc).TotalHours <= maxAgeHours.Value)
                {
                    return true;
                }

                _state.CompletedKeys.Remove(key);
                return false;
            }
        }

        public void MarkCompleted(string key)
        {
            lock (_lock)
            {
                _state.CompletedKeys[key] = DateTime.UtcNow.Ticks;
                SaveLocked();
            }
        }

        private void PruneExpiredLocked(int maxAgeHours)
        {
            foreach (var key in _state.CompletedKeys
                .Where(kvp => (DateTime.UtcNow - new DateTime(kvp.Value, DateTimeKind.Utc)).TotalHours > maxAgeHours)
                .Select(kvp => kvp.Key)
                .ToList())
            {
                _state.CompletedKeys.Remove(key);
            }
        }

        public void PruneExpired(int? maxAgeHours)
        {
            if (!maxAgeHours.HasValue)
            {
                return;
            }

            lock (_lock)
            {
                var before = _state.CompletedKeys.Count;
                PruneExpiredLocked(maxAgeHours.Value);
                if (before == _state.CompletedKeys.Count)
                {
                    return;
                }

                SaveLocked();
            }
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
                var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _statePath, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write JellyTag-Plus cache warmer state");
                TryDelete(tempPath);
            }
        }

        private static string GetStatePath()
        {
            var cachePath = Plugin.Instance?.CacheFolderPath ?? Path.Combine(Path.GetTempPath(), "JellyTag", "cache");
            return Path.Combine(cachePath, StateFileName);
        }

        private static void CleanTemporaryStateFiles(string statePath, ILogger<CacheWarmTask> logger)
        {
            try
            {
                var directory = Path.GetDirectoryName(statePath);
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return;
                }

                foreach (var tempFile in Directory.GetFiles(directory, $"{Path.GetFileName(statePath)}.*.tmp"))
                {
                    TryDelete(tempFile);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to clean temporary JellyTag-Plus cache warmer state files");
            }
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
    }

    public sealed record WarmerPhaseProgress(string Key, string Name, int Completed, int Total, double Percent);

    public sealed record WarmerClientProgress(string Key, string Name, bool Enabled, int Completed, int Total, double Percent, IReadOnlyList<WarmerPhaseProgress> Phases);

}
