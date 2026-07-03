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
    public const string HomePhaseKey = "home";
    public const string HomeLibrariesPhaseKey = "home-libraries";
    public const string LibrariesPhaseKey = "libraries";
    public const string EpisodesPhaseKey = "episodes";
    public const string VideosPhaseKey = "videos";
    public const string OtherPhaseKey = "other";
    public const string LearnedClientProfileKey = "learned";
    private const string WarmupResultHeader = "X-JellyTag-Warmup-Result";
    private const string WarmupResultCacheHit = "cache-hit";
    private const string WarmupResultCacheWritten = "cache-written";
    private const string WarmupResultNoVisibleBadges = "no-visible-badges";
    private static readonly TimeSpan PhaseRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProgressCacheDuration = TimeSpan.FromSeconds(60);
    private static readonly SemaphoreSlim ProgressCalculationGate = new(1, 1);
    private static readonly object ProgressCacheLock = new();
    private static readonly HashSet<string> DimensionQueryKeys = new(StringComparer.OrdinalIgnoreCase) { "width", "height", "maxWidth", "maxHeight", "fillWidth", "fillHeight" };
    private static readonly IReadOnlyList<WarmupPhase> WarmupPhases =
    [
        new WarmupPhase(HomePhaseKey, "Home", 0),
        new WarmupPhase(LibrariesPhaseKey, "Libraries", 1),
        new WarmupPhase(HomeLibrariesPhaseKey, "Home & Libraries", 2),
        new WarmupPhase(EpisodesPhaseKey, "Episodes", 3),
        new WarmupPhase(VideosPhaseKey, "Videos", 4),
        new WarmupPhase(OtherPhaseKey, "Other", 5)
    ];

    private static readonly string[] DefaultClientWarmupProfileKeys = [LearnedClientProfileKey, "androidtv", "roku", "streamyfin", "wholphin", "moonfin-mobile-desktop", "moonfin-tvos", "moonfin-smart-tv", "moonfin-roku", "dune", "swiftfin", "desktop", "findroid"];
    private static readonly string[] MoonfinSplitProfileKeys = ["moonfin-mobile-desktop", "moonfin-tvos", "moonfin-smart-tv", "moonfin-roku"];
    private static readonly TimeSpan ProgressHeartbeatInterval = TimeSpan.FromSeconds(5);
    private static string? _cachedProgressKey;
    private static DateTime _cachedProgressUtc;
    private static IReadOnlyList<WarmerClientProgress>? _cachedProgress;
    private static readonly IReadOnlyList<ClientWarmupProfile> FixedClientWarmupProfiles =
    [
        CreateFindroidProfile(),
        // Jellyfin Web and WebShellClients are intentionally not fixed profiles here. WebShellClients means
        // Android/iOS/Desktop Qt using Jellyfin Web inside the native app shell; those clients compute
        // image sizes dynamically. The optional Learned Clients profile can warm real sizes after browsing.
        CreateAndroidTvProfile(),
        CreateRokuProfile(),
        CreateStreamyfinProfile(),
        CreateWholphinProfile(),
        CreateMoonfinCoreProfile(),
        CreateMoonfinTvOsProfile(),
        CreateMoonfinSmartTvProfile(),
        CreateMoonfinRokuProfile(),
        CreateDuneProfile(),
        CreateSwiftfinProfile(),
        CreateDesktopProfile()
    ];

    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationHost _applicationHost;
    private readonly IImageTrafficCoordinator _trafficCoordinator;
    private readonly ILearnedClientProfileService _learnedClientProfileService;
    private readonly ILogger<CacheWarmTask> _logger;

    public CacheWarmTask(ILibraryManager libraryManager, IServerApplicationHost applicationHost, IImageTrafficCoordinator trafficCoordinator, ILearnedClientProfileService learnedClientProfileService, ILogger<CacheWarmTask> logger)
    {
        _libraryManager = libraryManager;
        _applicationHost = applicationHost;
        _trafficCoordinator = trafficCoordinator;
        _learnedClientProfileService = learnedClientProfileService;
        _logger = logger;
    }

    public string Name => "JellyTag-Plus Cache Warmer";
    public string Key => "JellyTagPlusCacheWarmer";
    public string Description => "Pre-renders JellyTag-Plus poster and thumbnail overlays for enabled libraries.";
    public string Category => "JellyTag-Plus";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);

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
        var profiles = GetEnabledClientWarmupProfiles(config, _learnedClientProfileService).ToList();
        if (profiles.Count == 0) { progress.Report(100); return; }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode, BaseItemKind.MusicVideo, BaseItemKind.Video]
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
        ReportWarmupProgress(progress, skipped, deduplicatedRequests);

        var baseUrl = GetBaseUrl();
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };

        var warmed = 0;
        var cacheHits = 0;
        var noVisibleBadges = 0;
        var failed = 0;
        var maxConcurrency = Math.Clamp(config.WarmerMaxConcurrency <= 0 ? 1 : config.WarmerMaxConcurrency, 1, 8);
        var delayMs = Math.Clamp(config.WarmerDelayMs, 0, 10000);
        var quietPeriod = TimeSpan.FromSeconds(Math.Clamp(config.WarmerClientQuietSeconds, 0, 120));
        using var throttler = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        int GetRecordedCompletedCount() => requests.Count(request => state.Contains(request.CompletionKey, warmerStateMaxAgeHours));

        foreach (var bucket in GetExecutionBuckets(requests))
        {
            var bucketRequests = requests
                .Where(request => IsInExecutionBucket(request, bucket))
                .OrderBy(request => request.ClientProfileOrder)
                .ThenBy(request => request.ItemId)
                .ThenBy(request => request.ImageType, StringComparer.Ordinal)
                .ThenBy(request => request.Variant.CacheKey, StringComparer.Ordinal)
                .ToList();
            if (bucketRequests.Count == 0)
            {
                continue;
            }

            _logger.LogInformation("JellyTag-Plus cache warmer starting {Phase} phase with {Count} requests", bucket.Name, bucketRequests.Count);
            foreach (var clientGroup in bucketRequests.GroupBy(request => request.ClientProfileOrder).OrderBy(group => group.Key))
            {
                var clientPhaseRequests = clientGroup.ToList();
                var clientName = clientPhaseRequests[0].ClientProfile;
                var clientPass = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var remainingClientRequests = clientPhaseRequests
                        .Where(request => !state.Contains(request.CompletionKey, warmerStateMaxAgeHours))
                        .ToList();
                    if (remainingClientRequests.Count == 0)
                    {
                        break;
                    }

                    clientPass++;
                    _logger.LogInformation("JellyTag-Plus cache warmer starting {Phase} phase for {ClientProfile} pass {Pass} with {Count} remaining requests", bucket.Name, clientName, clientPass, remainingClientRequests.Count);
                    var completedBeforePass = GetRecordedCompletedCount();
                    var tasks = remainingClientRequests.Select(async request =>
                    {
                        await throttler.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            await WaitForClientQuietPeriodWithProgressAsync(
                                quietPeriod,
                                progress,
                                () => skipped + GetRecordedCompletedCount(),
                                deduplicatedRequests,
                                cancellationToken).ConfigureAwait(false);
                            var url = request.ToUrl(baseUrl);
                            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
                            var warmupResult = GetWarmupResult(response);
                            if (response.IsSuccessStatusCode && IsCompletedWarmupResult(warmupResult))
                            {
                                state.MarkCompleted(request.CompletionKey);
                                if (string.Equals(warmupResult, WarmupResultCacheWritten, StringComparison.OrdinalIgnoreCase))
                                {
                                    Interlocked.Increment(ref warmed);
                                }
                                else if (string.Equals(warmupResult, WarmupResultCacheHit, StringComparison.OrdinalIgnoreCase))
                                {
                                    Interlocked.Increment(ref cacheHits);
                                }
                                else if (string.Equals(warmupResult, WarmupResultNoVisibleBadges, StringComparison.OrdinalIgnoreCase))
                                {
                                    Interlocked.Increment(ref noVisibleBadges);
                                }
                            }
                            else
                            {
                                Interlocked.Increment(ref failed);
                                _logger.LogDebug("JellyTag-Plus cache warmer got {StatusCode} with warmup result {WarmupResult} for {Url}", response.StatusCode, warmupResult ?? "none", url);
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
                            ReportWarmupProgress(progress, skipped + GetRecordedCompletedCount(), deduplicatedRequests);
                            throttler.Release();
                        }
                    }).ToArray();

                    await Task.WhenAll(tasks).ConfigureAwait(false);

                    if (remainingClientRequests.Any(request => !state.Contains(request.CompletionKey, warmerStateMaxAgeHours)) && GetRecordedCompletedCount() == completedBeforePass)
                    {
                        _logger.LogWarning("JellyTag-Plus cache warmer made no progress in {Phase} phase for {ClientProfile} pass {Pass}; retrying this client phase after {DelaySeconds} seconds", bucket.Name, clientName, clientPass, PhaseRetryDelay.TotalSeconds);
                        await Task.Delay(PhaseRetryDelay, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        _logger.LogInformation("JellyTag-Plus cache warmer complete. Requested {Total}, newly written {Warmed}, cache hits {CacheHits}, no visible badges {NoVisibleBadges}, failed {Failed}, skipped already warmed {Skipped}", requests.Count, warmed, cacheHits, noVisibleBadges, failed, skipped);
        progress.Report(100);
    }

    private static string? GetWarmupResult(HttpResponseMessage response)
    {
        return response.Headers.TryGetValues(WarmupResultHeader, out var values)
            ? values.FirstOrDefault()
            : null;
    }

    private static bool IsCompletedWarmupResult(string? warmupResult)
    {
        return string.Equals(warmupResult, WarmupResultCacheWritten, StringComparison.OrdinalIgnoreCase)
            || string.Equals(warmupResult, WarmupResultCacheHit, StringComparison.OrdinalIgnoreCase)
            || string.Equals(warmupResult, WarmupResultNoVisibleBadges, StringComparison.OrdinalIgnoreCase);
    }

    private async Task WaitForClientQuietPeriodWithProgressAsync(
        TimeSpan quietPeriod,
        IProgress<double> progress,
        Func<int> getCompletedCount,
        int totalCount,
        CancellationToken cancellationToken)
    {
        if (quietPeriod <= TimeSpan.Zero)
        {
            return;
        }

        var waitTask = _trafficCoordinator.WaitForClientQuietPeriodAsync(quietPeriod, cancellationToken);
        while (await Task.WhenAny(waitTask, Task.Delay(ProgressHeartbeatInterval, cancellationToken)).ConfigureAwait(false) != waitTask)
        {
            ReportWarmupProgress(progress, getCompletedCount(), totalCount);
        }

        await waitTask.ConfigureAwait(false);
    }

    private static void ReportWarmupProgress(IProgress<double> progress, int completedCount, int totalCount)
    {
        if (totalCount <= 0)
        {
            progress.Report(100);
            return;
        }

        progress.Report(Math.Clamp(completedCount * 100.0 / totalCount, 0, 100));
    }

    private sealed class WarmupRunLease : IDisposable
    {
        public void Dispose()
        {
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }

    public static async Task<IReadOnlyList<WarmerClientProgress>> GetEstimatedClientProgressAsync(PluginConfiguration config, ILibraryManager libraryManager, ILearnedClientProfileService learnedClientProfileService, IImageCacheService cacheService, ILogger<CacheWarmTask> logger)
    {
        var progressCacheKey = CreateProgressCacheKey(config);
        if (TryGetCachedProgress(progressCacheKey, out var cachedProgress))
        {
            return cachedProgress;
        }

        if (!await ProgressCalculationGate.WaitAsync(0).ConfigureAwait(false))
        {
            if (TryGetAnyCachedProgress(out cachedProgress))
            {
                return cachedProgress;
            }

            await ProgressCalculationGate.WaitAsync().ConfigureAwait(false);
        }

        try
        {
            if (TryGetCachedProgress(progressCacheKey, out cachedProgress))
            {
                return cachedProgress;
            }

            var progress = await CalculateEstimatedClientProgressAsync(config, libraryManager, learnedClientProfileService, cacheService, logger).ConfigureAwait(false);
            SetCachedProgress(progressCacheKey, progress);
            return progress;
        }
        finally
        {
            ProgressCalculationGate.Release();
        }
    }

    private static async Task<IReadOnlyList<WarmerClientProgress>> CalculateEstimatedClientProgressAsync(PluginConfiguration config, ILibraryManager libraryManager, ILearnedClientProfileService learnedClientProfileService, IImageCacheService cacheService, ILogger<CacheWarmTask> logger)
    {
        var scope = CreateWarmupScope(config);
        var state = WarmupStateStore.Load(scope, logger);
        var fallbackState = WarmupStateStore.Load(scope, logger, allowStoredScope: true);
        if (state.CompletedCount == 0 && fallbackState.CompletedCount > 0)
        {
            state = fallbackState;
        }

        var warmerStateMaxAgeHours = GetWarmerStateMaxAgeHours(config);
        state.PruneExpired(warmerStateMaxAgeHours);

        var items = libraryManager.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode, BaseItemKind.MusicVideo, BaseItemKind.Video]
        }).Where(item => IsInEnabledLibrary(item, config, libraryManager)).ToList();

        var enabledKeys = GetEnabledClientProfileKeys(config).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var progress = new List<WarmerClientProgress>();
        foreach (var profile in GetOrderedClientWarmupProfiles(config, includeDisabled: true, learnedClientProfileService))
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

            var completedKeys = await GetCompletedWarmupRequestKeysAsync(requests, state, warmerStateMaxAgeHours, cacheService).ConfigureAwait(false);
            var completed = completedKeys.Count;
            var total = requests.Count;
            var percent = total == 0 ? 100 : Math.Round(completed * 100.0 / total, 1);
            var phases = GetDisplayPhaseProgress(requests, completedKeys);

            progress.Add(new WarmerClientProgress(profile.Key, profile.Name, enabledKeys.Contains(profile.Key), completed, total, percent, phases));
        }

        return progress;
    }

    private static bool TryGetCachedProgress(string progressCacheKey, out IReadOnlyList<WarmerClientProgress> progress)
    {
        lock (ProgressCacheLock)
        {
            if (_cachedProgress != null
                && string.Equals(_cachedProgressKey, progressCacheKey, StringComparison.Ordinal)
                && DateTime.UtcNow - _cachedProgressUtc < ProgressCacheDuration)
            {
                progress = _cachedProgress;
                return true;
            }
        }

        progress = [];
        return false;
    }

    private static bool TryGetAnyCachedProgress(out IReadOnlyList<WarmerClientProgress> progress)
    {
        lock (ProgressCacheLock)
        {
            if (_cachedProgress != null && DateTime.UtcNow - _cachedProgressUtc < ProgressCacheDuration)
            {
                progress = _cachedProgress;
                return true;
            }
        }

        progress = [];
        return false;
    }

    private static void SetCachedProgress(string progressCacheKey, IReadOnlyList<WarmerClientProgress> progress)
    {
        lock (ProgressCacheLock)
        {
            _cachedProgressKey = progressCacheKey;
            _cachedProgressUtc = DateTime.UtcNow;
            _cachedProgress = progress;
        }
    }

    private static async Task<HashSet<string>> GetCompletedWarmupRequestKeysAsync(IReadOnlyList<WarmupRequest> requests, WarmupStateStore state, int? maxAgeHours, IImageCacheService cacheService)
    {
        var completedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in requests)
        {
            if (state.Contains(request.CompletionKey, maxAgeHours))
            {
                completedKeys.Add(request.CompletionKey);
                continue;
            }

            var requestCacheKey = cacheService.CreateRequestCacheKey(request.ItemId, request.ImageType, request.ImageVersion, request.Variant.CacheKey, request.ItemModifiedTicks);
            if (await cacheService.GetCachedImageFileForRequestAsync(request.ItemId, requestCacheKey).ConfigureAwait(false) != null)
            {
                completedKeys.Add(request.CompletionKey);
            }
        }

        return completedKeys;
    }

    private static IReadOnlyList<WarmerPhaseProgress> GetDisplayPhaseProgress(IReadOnlyList<WarmupRequest> requests, HashSet<string> completedKeys)
    {
        var hasHome = requests.Any(request => string.Equals(request.Phase.Key, HomePhaseKey, StringComparison.OrdinalIgnoreCase));
        var hasLibraries = requests.Any(request => string.Equals(request.Phase.Key, LibrariesPhaseKey, StringComparison.OrdinalIgnoreCase));
        var groups = requests
            .GroupBy(request => GetDisplayPhase(request.Phase, hasHome, hasLibraries).Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        return WarmupPhases
            .Where(phase => !string.Equals(phase.Key, OtherPhaseKey, StringComparison.OrdinalIgnoreCase))
            .Where(phase => groups.ContainsKey(phase.Key))
            .Select(phase =>
            {
                var phaseRequests = groups[phase.Key];
                var phaseCompleted = phaseRequests.Count(request => completedKeys.Contains(request.CompletionKey));
                var phaseTotal = phaseRequests.Count;
                var phasePercent = phaseTotal == 0 ? 100 : Math.Round(phaseCompleted * 100.0 / phaseTotal, 1);
                return new WarmerPhaseProgress(phase.Key, phase.Name, phaseCompleted, phaseTotal, phasePercent);
            })
            .ToList();
    }

    private static WarmupPhase GetDisplayPhase(WarmupPhase phase, bool hasHome, bool hasLibraries)
    {
        if (string.Equals(phase.Key, HomeLibrariesPhaseKey, StringComparison.OrdinalIgnoreCase))
        {
            if (hasHome && hasLibraries)
            {
                return GetPhase(LibrariesPhaseKey);
            }
        }

        if (string.Equals(phase.Key, OtherPhaseKey, StringComparison.OrdinalIgnoreCase))
        {
            if (hasLibraries)
            {
                return GetPhase(LibrariesPhaseKey);
            }

            if (hasHome)
            {
                return GetPhase(HomePhaseKey);
            }

            return GetPhase(HomeLibrariesPhaseKey);
        }

        return phase;
    }

    private static IEnumerable<ClientWarmupProfile> GetEnabledClientWarmupProfiles(PluginConfiguration config, ILearnedClientProfileService learnedClientProfileService)
    {
        return GetOrderedClientWarmupProfiles(config, includeDisabled: false, learnedClientProfileService);
    }

    private static IEnumerable<ClientWarmupProfile> GetOrderedClientWarmupProfiles(PluginConfiguration config, bool includeDisabled, ILearnedClientProfileService learnedClientProfileService)
    {
        var allProfiles = FixedClientWarmupProfiles
            .Concat([CreateLearnedClientProfile(learnedClientProfileService.GetVariants())])
            .ToList();
        var profileMap = allProfiles.ToDictionary(profile => profile.Key, StringComparer.OrdinalIgnoreCase);
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

        foreach (var profile in allProfiles)
        {
            if (seen.Add(profile.Key))
            {
                yield return profile;
            }
        }
    }

    private static IEnumerable<string> GetEnabledClientProfileKeys(PluginConfiguration config)
    {
        return (config.WarmerClientProfiles ?? DefaultClientWarmupProfileKeys.AsEnumerable())
            .SelectMany(ExpandClientProfileKey);
    }

    private static IEnumerable<string> GetConfiguredClientProfileOrder(PluginConfiguration config)
    {
        var order = config.WarmerClientProfileOrder?.Count > 0
            ? config.WarmerClientProfileOrder
            : GetEnabledClientProfileKeys(config).Concat(DefaultClientWarmupProfileKeys).Concat([LearnedClientProfileKey]);
        return order.SelectMany(ExpandClientProfileKey);
    }

    private static IEnumerable<string> ExpandClientProfileKey(string key)
    {
        if (string.Equals(key, "moonfin", StringComparison.OrdinalIgnoreCase))
        {
            return MoonfinSplitProfileKeys;
        }

        return string.IsNullOrWhiteSpace(key) ? [] : [key];
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
            yield return new WarmupRequest(item.Id, imageType, imageVersion, itemModifiedTicks, profile.Key, profile.Name, profileOrder, phase, variant);
        }
    }

    private static IEnumerable<WarmupExecutionBucket> GetExecutionBuckets(IReadOnlyList<WarmupRequest> requests)
    {
        foreach (var phaseGroup in WarmupPhases.GroupBy(phase => phase.Order).OrderBy(group => group.Key))
        {
            var phaseKeys = phaseGroup.Select(phase => phase.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!requests.Any(request => phaseKeys.Contains(request.Phase.Key)))
            {
                continue;
            }

            yield return new WarmupExecutionBucket(
                string.Join("-", phaseGroup.Select(phase => phase.Key)),
                string.Join(" / ", phaseGroup.Select(phase => phase.Name)),
                phaseGroup.Key);
        }
    }

    private static bool IsInExecutionBucket(WarmupRequest request, WarmupExecutionBucket bucket)
    {
        return request.Phase.Order == bucket.Order;
    }

    private static WarmupPhase GetRequestPhase(BaseItem item, ImageVariant variant)
    {
        if (item is Episode)
        {
            return GetPhase(EpisodesPhaseKey);
        }

        if (item is MusicVideo || item.GetType() == typeof(Video))
        {
            return GetPhase(VideosPhaseKey);
        }

        return variant.Phase;
    }

    private static WarmupPhase GetPhase(string key)
    {
        var normalizedKey = string.Equals(key, "learned-home-libraries", StringComparison.OrdinalIgnoreCase)
            ? HomeLibrariesPhaseKey
            : key;

        return WarmupPhases.FirstOrDefault(phase => string.Equals(phase.Key, normalizedKey, StringComparison.OrdinalIgnoreCase))
            ?? GetPhase(OtherPhaseKey);
    }

    /// <summary>
    /// Checks whether an image variant is already covered by a fixed warmer client profile.
    /// </summary>
    /// <param name="imageType">The image type.</param>
    /// <param name="query">The normalized query.</param>
    /// <returns>True when the variant is already covered by a fixed client profile.</returns>
    public static bool IsFixedClientVariant(string imageType, IReadOnlyList<KeyValuePair<string, string>> query)
    {
        var cacheKey = CreateNormalizedVariantCacheKey(query);
        return FixedClientWarmupProfiles
            .SelectMany(profile => profile.GetVariants(imageType))
            .Any(variant => string.Equals(CreateNormalizedVariantCacheKey(variant.Query), cacheKey, StringComparison.Ordinal));
    }

    private static string CreateNormalizedVariantCacheKey(IReadOnlyList<KeyValuePair<string, string>> query)
    {
        return ImageVariant.CreateCacheKey(query
            .Select(kvp =>
            {
                if (DimensionQueryKeys.Contains(kvp.Key) && int.TryParse(kvp.Value, out var value))
                {
                    var rounded = Math.Max(1, (int)(Math.Round(value / 10.0, MidpointRounding.AwayFromZero) * 10));
                    return new KeyValuePair<string, string>(kvp.Key, rounded.ToString());
                }

                return kvp;
            })
            .ToList());
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
            [ImageVariant.Unsized(GetPhase(HomeLibrariesPhaseKey), "home/library unsized")],
            []);
    }

    private static ClientWarmupProfile CreateAndroidTvProfile()
    {
        var primaryVariants = new[]
        {
            ImageVariant.MaxSize(GetPhase(HomePhaseKey), 100, 150, "home poster"),
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 116, 174, "library poster smallest"),
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 118, 178, "library poster vertical smallest"),
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 136, 205, "library poster vertical small"),
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 144, 217, "library poster small"),
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 161, 242, "library poster vertical medium"),
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 192, 288, "library poster medium"),
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 252, 379, "library poster vertical large"),
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 284, 427, "library poster large"),
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 352, 528, "library poster x-large")
        };

        var thumbVariants = new[]
        {
            ImageVariant.MaxSize(GetPhase(HomePhaseKey), 267, 150, "home thumb/backdrop"),
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 161, 91, "library thumb vertical smallest"),
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 197, 111, "library thumb vertical small"),
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 222, 125, "library thumb smallest"),
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 252, 142, "library thumb vertical medium"),
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 259, 146, "library thumb small"),
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 309, 174, "library thumb medium"),
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 352, 198, "library thumb vertical large"),
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 385, 217, "library thumb large"),
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 581, 327, "library thumb vertical x-large"),
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 759, 427, "library thumb x-large")
        };

        return new ClientWarmupProfile("androidtv", "Jellyfin Android TV", "maxWidth/maxHeight requests for home rows and poster-size library settings.", primaryVariants, thumbVariants);
    }

    private static ClientWarmupProfile CreateRokuProfile()
    {
        var primaryVariants = new[]
        {
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 196, 384, "default poster helper", 90),
            ImageVariant.MaxSize(GetPhase(HomePhaseKey), 180, 331, "home movie poster display", 90),
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 295, 440, "library poster data", 90),
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 300, 450, "person/search style poster", 90),
            ImageVariant.MaxSize(GetPhase(HomePhaseKey), 464, 331, "home/library wide poster", 90),
            ImageVariant.MaxSize(GetPhase(EpisodesPhaseKey), 400, 384, "episode row actual fallback", 90),
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 500, 500, "square audio/library art", 90)
        };

        var thumbVariants = new[]
        {
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 196, 384, "default thumb fallback", 90),
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 295, 440, "portrait fallback thumb", 90),
            ImageVariant.MaxSize(GetPhase(EpisodesPhaseKey), 400, 384, "episode row actual fallback", 90),
            ImageVariant.MaxSize(GetPhase(HomePhaseKey), 464, 331, "series/home landscape thumb", 90),
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 500, 500, "square thumb art", 90)
        };

        return new ClientWarmupProfile("roku", "Jellyfin Roku", "Mostly fixed maxWidth/maxHeight requests used by Roku data nodes and home rows.", primaryVariants, thumbVariants);
    }

    private static ClientWarmupProfile CreateStreamyfinProfile()
    {
        var primaryVariants = new[]
        {
            ImageVariant.Width(GetPhase(LibrariesPhaseKey), 1000, 90, "main library ItemImage"),
            ImageVariant.FillWidth(GetPhase(HomePhaseKey), 300, 80, "poster rows"),
            ImageVariant.FillHeight(GetPhase(EpisodesPhaseKey), 389, 80, "episode poster / horizontal fallback")
        };

        var thumbVariants = new[]
        {
            ImageVariant.FillHeight(GetPhase(HomePhaseKey), 389, 80, "continue watching / next up horizontal cards")
        };

        return new ClientWarmupProfile("streamyfin", "Streamyfin", "width/fillWidth/fillHeight requests used in library and home poster components.", primaryVariants, thumbVariants);
    }

    private static ClientWarmupProfile CreateWholphinProfile()
    {
        var variants = new[]
        {
            ImageVariant.QualityOnly(GetPhase(LibrariesPhaseKey), 96, "detail and provider image default"),
            ImageVariant.FillHeight(GetPhase(HomePhaseKey), 172, 96, "default home row height"),
            ImageVariant.FillHeight(GetPhase(HomePhaseKey), 148, 96, "compact home row height"),
            ImageVariant.FillHeight(GetPhase(HomePhaseKey), 128, 96, "episode, genre, and square row height"),
            ImageVariant.FillHeight(GetPhase(HomePhaseKey), 100, 96, "compact episode and wide row height"),
            ImageVariant.FillHeight(GetPhase(HomePhaseKey), 96, 96, "Live TV row height")
        };

        return new ClientWarmupProfile("wholphin", "Wholphin", "quality=96 and fixed fillHeight requests used by Wholphin rows and cards; dynamic grid fillWidth values are intentionally not guessed.", variants, variants);
    }

    private static ClientWarmupProfile CreateMoonfinCoreProfile()
    {
        var primaryVariants = new[]
        {
            ImageVariant.MaxHeight(GetPhase(HomeLibrariesPhaseKey), 120, "mini player, sidebar, and playlist primary"),
            ImageVariant.MaxHeight(GetPhase(HomeLibrariesPhaseKey), 200, "compact person primary"),
            ImageVariant.MaxHeight(GetPhase(HomeLibrariesPhaseKey), 240, "mobile album row primary"),
            ImageVariant.MaxHeight(GetPhase(HomeLibrariesPhaseKey), 300, "music, recordings, remote, and list primary"),
            ImageVariant.MaxHeight(GetPhase(HomeLibrariesPhaseKey), 400, "book, person, and detail row primary"),
            ImageVariant.MaxWidth(GetPhase(HomeLibrariesPhaseKey), 400, "episode and next up primary width"),
            ImageVariant.MaxHeight(GetPhase(OtherPhaseKey), 360, "mobile detail poster primary"),
            ImageVariant.MaxHeight(GetPhase(OtherPhaseKey), 420, "player artwork primary"),
            ImageVariant.MaxHeight(GetPhase(OtherPhaseKey), 480, "Apple TV Top Shelf primary"),
            ImageVariant.MaxHeight(GetPhase(OtherPhaseKey), 500, "offline/detail poster primary"),
            ImageVariant.MaxHeight(GetPhase(OtherPhaseKey), 520, "compact book cover primary"),
            ImageVariant.MaxHeight(GetPhase(OtherPhaseKey), 600, "audio artwork primary"),
            ImageVariant.MaxHeight(GetPhase(OtherPhaseKey), 720, "book cover primary"),
            ImageVariant.MaxHeight(GetPhase(OtherPhaseKey), 1080, "focused primary artwork")
        };

        var thumbVariants = new[]
        {
            ImageVariant.MaxWidth(GetPhase(HomeLibrariesPhaseKey), 120, "playlist thumbnail"),
            ImageVariant.MaxWidth(GetPhase(OtherPhaseKey), 960, "Top Shelf and player landscape thumbnail")
        };

        return new ClientWarmupProfile("moonfin-mobile-desktop", "Moonfin Core", "Fixed Primary and Thumb sizes found in Moonfin Core. Dynamic card, genre, folder-grid, scaled desktop, and screen-derived variants are handled by Learned Clients.", primaryVariants, thumbVariants);
    }

    private static ClientWarmupProfile CreateMoonfinTvOsProfile()
    {
        var primaryVariants = new[]
        {
            ImageVariant.MaxWidth(GetPhase(HomeLibrariesPhaseKey), 200, "compact primary"),
            ImageVariant.MaxWidth(GetPhase(HomeLibrariesPhaseKey), 300, "search primary"),
            ImageVariant.MaxWidth(GetPhase(HomeLibrariesPhaseKey), 320, "season row primary"),
            ImageVariant.MaxWidth(GetPhase(HomeLibrariesPhaseKey), 400, "detail row primary"),
            ImageVariant.MaxWidth(GetPhase(HomeLibrariesPhaseKey), 450, "item detail primary"),
            ImageVariant.MaxSize(GetPhase(OtherPhaseKey), 960, 540, "playback primary")
        };

        var thumbVariants = new[]
        {
            ImageVariant.MaxWidth(GetPhase(HomeLibrariesPhaseKey), 400, "detail row thumb"),
            ImageVariant.MaxWidth(GetPhase(EpisodesPhaseKey), 560, "episode list thumb")
        };

        return new ClientWarmupProfile("moonfin-tvos", "Moonfin tvOS", "Fixed Primary and Thumb maxWidth requests used by Moonfin tvOS detail, search, playback, and episode views.", primaryVariants, thumbVariants);
    }

    private static ClientWarmupProfile CreateMoonfinSmartTvProfile()
    {
        var primaryVariants = new[]
        {
            ImageVariant.MaxHeight(GetPhase(HomeLibrariesPhaseKey), 300, "home/library primary card", 80),
            ImageVariant.MaxHeight(GetPhase(LibrariesPhaseKey), 300, "library primary card", 70),
            ImageVariant.MaxWidth(GetPhase(HomeLibrariesPhaseKey), 400, "wide primary card", 80),
            ImageVariant.MaxHeight(GetPhase(HomeLibrariesPhaseKey), 400, "favorites primary card", 80),
            ImageVariant.MaxWidth(GetPhase(OtherPhaseKey), 500, "detail episode primary", 90),
            ImageVariant.MaxHeight(GetPhase(OtherPhaseKey), 600, "detail poster primary", 90),
            ImageVariant.MaxHeight(GetPhase(OtherPhaseKey), 350, "season primary", 80),
            ImageVariant.MaxHeight(GetPhase(OtherPhaseKey), 80, "playlist primary", 80),
            ImageVariant.MaxHeight(GetPhase(OtherPhaseKey), 500, "player primary", 90),
            ImageVariant.MaxWidth(GetPhase(OtherPhaseKey), 300, "recording primary", 90)
        };

        var thumbVariants = new[]
        {
            ImageVariant.MaxWidth(GetPhase(HomeLibrariesPhaseKey), 400, "wide thumb card", 80),
            ImageVariant.MaxHeight(GetPhase(LibrariesPhaseKey), 300, "library thumb card", 70),
            ImageVariant.MaxHeight(GetPhase(HomeLibrariesPhaseKey), 400, "favorites thumb card", 80),
            ImageVariant.MaxWidth(GetPhase(OtherPhaseKey), 500, "detail episode thumb", 90)
        };

        return new ClientWarmupProfile("moonfin-smart-tv", "Moonfin Smart-TV", "Fixed Primary and Thumb maxWidth/maxHeight requests used by Moonfin Smart-TV cards, details, favorites, and playback surfaces.", primaryVariants, thumbVariants);
    }

    private static ClientWarmupProfile CreateMoonfinRokuProfile()
    {
        var primaryVariants = new[]
        {
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 196, 384, "default poster helper", 90),
            ImageVariant.MaxSize(GetPhase(HomePhaseKey), 180, 331, "home movie poster display", 90),
            ImageVariant.MaxSize(GetPhase(HomeLibrariesPhaseKey), 180, 320, "scene primary", 90),
            ImageVariant.MaxSize(GetPhase(HomeLibrariesPhaseKey), 180, 270, "extras primary", 90),
            ImageVariant.MaxSize(GetPhase(HomeLibrariesPhaseKey), 200, 400, "extras poster", 90),
            ImageVariant.MaxSize(GetPhase(HomeLibrariesPhaseKey), 234, 351, "extras portrait", 90),
            ImageVariant.MaxSize(GetPhase(HomeLibrariesPhaseKey), 250, 331, "person/extras primary", 90),
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 295, 440, "library poster data", 90),
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 300, 450, "person/search style poster", 90),
            ImageVariant.MaxWidth(GetPhase(LibrariesPhaseKey), 400, "server poster helper", 90),
            ImageVariant.MaxSize(GetPhase(EpisodesPhaseKey), 400, 384, "episode row actual fallback", 90),
            ImageVariant.MaxSize(GetPhase(OtherPhaseKey), 400, 600, "detail poster", 90),
            ImageVariant.MaxSize(GetPhase(HomePhaseKey), 464, 331, "home/library wide poster", 90),
            ImageVariant.MaxSize(GetPhase(HomeLibrariesPhaseKey), 480, 270, "extras wide primary", 90),
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 500, 500, "square audio/library art", 90),
            ImageVariant.MaxSize(GetPhase(EpisodesPhaseKey), 502, 283, "extras episode primary", 90),
            ImageVariant.MaxSize(GetPhase(EpisodesPhaseKey), 520, 293, "detail episode primary", 90)
        };

        var thumbVariants = new[]
        {
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 196, 384, "default thumb fallback", 90),
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 295, 440, "portrait fallback thumb", 90),
            ImageVariant.MaxSize(GetPhase(EpisodesPhaseKey), 400, 384, "episode row actual fallback", 90),
            ImageVariant.MaxSize(GetPhase(HomePhaseKey), 464, 331, "series/home landscape thumb", 90),
            ImageVariant.MaxSize(GetPhase(HomeLibrariesPhaseKey), 480, 270, "extras wide thumb", 90),
            ImageVariant.MaxSize(GetPhase(LibrariesPhaseKey), 500, 500, "square thumb art", 90)
        };

        return new ClientWarmupProfile("moonfin-roku", "Moonfin Roku", "Fixed Roku-style Primary and Thumb requests used by Moonfin Roku data nodes, home rows, details, and extras.", primaryVariants, thumbVariants);
    }

    private static ClientWarmupProfile CreateDuneProfile()
    {
        var primaryVariants = new[]
        {
            ImageVariant.MaxSize(GetPhase(HomeLibrariesPhaseKey), 240, 360, "search poster card"),
            ImageVariant.MaxSize(GetPhase(HomeLibrariesPhaseKey), 300, 450, "search preload poster"),
            ImageVariant.MaxSize(GetPhase(EpisodesPhaseKey), 440, 220, "search episode/music video card")
        };

        var thumbVariants = new[]
        {
            ImageVariant.MaxSize(GetPhase(HomeLibrariesPhaseKey), 440, 220, "search box set thumb"),
            ImageVariant.FillSize(GetPhase(HomePhaseKey), 1920, 1080, "home carousel thumb fallback")
        };

        return new ClientWarmupProfile("dune", "DUNE", "Fixed search and carousel Primary/Thumb requests from DUNE. Its main browse cards also use screen-derived maxHeight values, which are better handled by Learned Clients.", primaryVariants, thumbVariants);
    }

    private static ClientWarmupProfile CreateSwiftfinProfile()
    {
        var primaryVariants = new[]
        {
            ImageVariant.MaxWidth(GetPhase(HomeLibrariesPhaseKey), 60, "tvOS portrait library row", 90),
            ImageVariant.MaxWidth(GetPhase(HomeLibrariesPhaseKey), 110, "tvOS landscape library row", 90),
            ImageVariant.MaxWidth(GetPhase(HomeLibrariesPhaseKey), 200, "thumb/iPad primary"),
            ImageVariant.MaxWidth(GetPhase(EpisodesPhaseKey), 250, "episode card"),
            ImageVariant.MaxWidth(GetPhase(HomeLibrariesPhaseKey), 300, "download artwork"),
            ImageVariant.MaxWidth(GetPhase(HomeLibrariesPhaseKey), 450, "tvOS item primary"),
            ImageVariant.MaxWidth(GetPhase(HomeLibrariesPhaseKey), 500, "media item"),
            ImageVariant.MaxWidth(GetPhase(EpisodesPhaseKey), 500, "tvOS episode selector"),
            ImageVariant.MaxWidth(GetPhase(HomeLibrariesPhaseKey), 600, "simple/download item view"),
            ImageVariant.MaxWidth(GetPhase(HomeLibrariesPhaseKey), 1320, "compact portrait primary"),
            ImageVariant.MaxWidth(GetPhase(HomeLibrariesPhaseKey), 1920, "cinematic primary")
        };

        var thumbVariants = new[]
        {
            ImageVariant.MaxWidth(GetPhase(HomeLibrariesPhaseKey), 110, "tvOS landscape library row", 90),
            ImageVariant.MaxWidth(GetPhase(HomeLibrariesPhaseKey), 200, "thumb image source", 90),
            ImageVariant.MaxWidth(GetPhase(HomeLibrariesPhaseKey), 600, "download landscape item")
        };

        return new ClientWarmupProfile("swiftfin", "Swiftfin", "Fixed Primary/Thumb maxWidth requests used by Swiftfin library rows, detail views, episode cards, and downloaded item views.", primaryVariants, thumbVariants);
    }

    private static ClientWarmupProfile CreateDesktopProfile()
    {
        var primaryVariants = new[]
        {
            ImageVariant.MaxWidth(GetPhase(OtherPhaseKey), 512, "native media artwork")
        };

        return new ClientWarmupProfile("desktop", "Jellyfin Desktop", "Fixed native media artwork request from Jellyfin Desktop. Normal Desktop Qt browsing uses dynamic Jellyfin Web sizes.", primaryVariants, []);
    }

    private static ClientWarmupProfile CreateLearnedClientProfile(IReadOnlyList<LearnedClientVariant> learnedVariants)
    {
        learnedVariants = learnedVariants
            .Where(variant => !IsFixedClientVariant(variant.ImageType, variant.Query))
            .ToList();

        var primaryVariants = learnedVariants
            .Where(variant => string.Equals(variant.ImageType, "Primary", StringComparison.OrdinalIgnoreCase))
            .Select(ToImageVariant)
            .ToList();
        var thumbVariants = learnedVariants
            .Where(variant => string.Equals(variant.ImageType, "Thumb", StringComparison.OrdinalIgnoreCase))
            .Select(ToImageVariant)
            .ToList();

        return new ClientWarmupProfile(
            LearnedClientProfileKey,
            "Learned Clients",
            "Variants learned from real client Primary/Thumb requests and normalized to the nearest 10 pixels.",
            primaryVariants,
            thumbVariants);
    }

    private static ImageVariant ToImageVariant(LearnedClientVariant variant)
    {
        return ImageVariant.FromQuery(GetPhase(variant.PhaseKey), variant.Label, variant.Query);
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

    private sealed record WarmupExecutionBucket(string Key, string Name, int Order);

    private sealed record ImageVariant(WarmupPhase Phase, string Label, IReadOnlyList<KeyValuePair<string, string>> Query)
    {
        public string CacheKey => CreateCacheKey(Query);

        public static string CreateCacheKey(IReadOnlyList<KeyValuePair<string, string>> query)
        {
            return string.Join("&", query.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase).Select(kvp => $"{kvp.Key}={kvp.Value}"));
        }

        public static ImageVariant FromQuery(WarmupPhase phase, string label, IReadOnlyList<KeyValuePair<string, string>> query)
        {
            return new ImageVariant(phase, label, query);
        }

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

        public static ImageVariant FillSize(WarmupPhase phase, int width, int height, string label, int? quality = null)
        {
            return Create(phase, label, quality, ("fillWidth", width), ("fillHeight", height));
        }

        public static ImageVariant QualityOnly(WarmupPhase phase, int quality, string label)
        {
            return Create(phase, label, quality);
        }

        public static ImageVariant Width(WarmupPhase phase, int width, int quality, string label)
        {
            return Create(phase, label, quality, ("width", width));
        }

        public static ImageVariant MaxWidth(WarmupPhase phase, int width, string label, int? quality = null)
        {
            return Create(phase, label, quality, ("maxWidth", width));
        }

        public static ImageVariant MaxHeight(WarmupPhase phase, int height, string label, int? quality = null)
        {
            return Create(phase, label, quality, ("maxHeight", height));
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
        var excludedLibraryIds = (config.ExcludedLibraryIds ?? [])
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var libraryBadgeOptions = (config.LibraryBadgeOptions ?? [])
            .OrderBy(option => option.LibraryId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var customBadgeTexts = (config.CustomBadgeTexts ?? [])
            .OrderBy(overrideItem => overrideItem.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var scope = new
        {
            Version = "warmer-v3",
            config.Enabled,
            config.ThumbnailSameAsPoster,
            config.ThumbnailSizeReduction,
            ExcludedLibraryIds = excludedLibraryIds,
            LibraryBadgeOptions = libraryBadgeOptions,
            CustomBadgeTexts = customBadgeTexts,
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

    private static string CreateProgressCacheKey(PluginConfiguration config)
    {
        var profileKeys = (config.WarmerClientProfiles ?? [])
            .SelectMany(ExpandClientProfileKey)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var profileOrder = (config.WarmerClientProfileOrder ?? [])
            .SelectMany(ExpandClientProfileKey)
            .ToList();

        var scope = new
        {
            WarmupScope = CreateWarmupScope(config),
            ProfileKeys = profileKeys,
            ProfileOrder = profileOrder,
            config.CacheDurationHours
        };
        var json = JsonSerializer.Serialize(scope);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16];
    }

    private static string ComputeShortHash(string input)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..16];
    }

    private sealed record WarmupRequest(Guid ItemId, string ImageType, string ImageVersion, long ItemModifiedTicks, string ClientProfileKey, string ClientProfile, int ClientProfileOrder, WarmupPhase Phase, ImageVariant Variant)
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

        private WarmupStateStore(string scope, string statePath, WarmupState state, ILogger<CacheWarmTask> logger, bool allowStoredScope)
        {
            _statePath = statePath;
            _logger = logger;
            _state = state.Scope == scope || allowStoredScope ? state : new WarmupState { Scope = scope };
            _state.CompletedKeys = new Dictionary<string, long>(_state.CompletedKeys ?? [], StringComparer.Ordinal);
        }

        public static WarmupStateStore Load(string scope, ILogger<CacheWarmTask> logger, bool allowStoredScope = false)
        {
            var statePath = GetStatePath();
            CleanTemporaryStateFiles(statePath, logger);

            try
            {
                if (File.Exists(statePath))
                {
                    var json = File.ReadAllText(statePath);
                    var state = JsonSerializer.Deserialize<WarmupState>(json) ?? new WarmupState { Scope = scope };
                    return new WarmupStateStore(scope, statePath, state, logger, allowStoredScope);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read JellyTag-Plus cache warmer state; starting a fresh warmup ledger");
            }

            return new WarmupStateStore(scope, statePath, new WarmupState { Scope = scope }, logger, allowStoredScope);
        }

        public int CompletedCount => _state.CompletedKeys.Count;

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
