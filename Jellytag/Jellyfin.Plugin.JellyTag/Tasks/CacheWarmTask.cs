using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyTag.Configuration;
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
    private static readonly IReadOnlyList<ClientWarmupProfile> ClientWarmupProfiles =
    [
        CreateFindroidProfile(),
        CreateJellyfinWebProfile(),
        CreateWebShellClientsProfile(),
        CreateAndroidTvProfile(),
        CreateRokuProfile(),
        CreateStreamyfinProfile()
    ];

    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<CacheWarmTask> _logger;

    public CacheWarmTask(ILibraryManager libraryManager, IServerApplicationHost applicationHost, ILogger<CacheWarmTask> logger)
    {
        _libraryManager = libraryManager;
        _applicationHost = applicationHost;
        _logger = logger;
    }

    public string Name => "JellyTag-Plus Cache Warmer";
    public string Key => "JellyTagPlusCacheWarmer";
    public string Description => "Pre-renders JellyTag-Plus poster and thumbnail overlays for enabled libraries.";
    public string Category => "JellyTag-Plus";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.Enabled) { progress.Report(100); return; }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode, BaseItemKind.Video]
        }).Where(item => IsInEnabledLibrary(item, config)).ToList();

        var requests = new List<WarmupRequest>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldRequestPrimary(item, config) && item.HasImage(ImageType.Primary, 0))
            {
                requests.AddRange(CreateWarmupRequests(item.Id, "Primary"));
            }

            if (ShouldRequestThumb(item, config) && item.HasImage(ImageType.Thumb, 0))
            {
                requests.AddRange(CreateWarmupRequests(item.Id, "Thumb"));
            }
        }

        requests = requests
            .GroupBy(request => request.CacheKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        if (requests.Count == 0) { progress.Report(100); return; }

        var baseUrl = GetBaseUrl();
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };

        var completed = 0;
        var warmed = 0;
        var failed = 0;
        using var throttler = new SemaphoreSlim(3, 3);

        var tasks = requests.Select(async request =>
        {
            await throttler.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var url = request.ToUrl(baseUrl);
                using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) Interlocked.Increment(ref warmed);
                else
                {
                    Interlocked.Increment(ref failed);
                    _logger.LogDebug("JellyTag-Plus cache warmer got {StatusCode} for {Url}", response.StatusCode, url);
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
        _logger.LogInformation("JellyTag-Plus cache warmer complete. Requested {Total}, warmed {Warmed}, failed {Failed}", requests.Count, warmed, failed);
    }

    private static IEnumerable<WarmupRequest> CreateWarmupRequests(Guid itemId, string imageType)
    {
        foreach (var profile in ClientWarmupProfiles)
        {
            foreach (var variant in profile.GetVariants(imageType))
            {
                yield return new WarmupRequest(itemId, imageType, profile.Name, variant);
            }
        }
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
        var folders = _libraryManager.GetCollectionFolders(item).ToList();
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
            "Findroid",
            "Findroid home and library views request unsized item images and let the client scale them.",
            [ImageVariant.Unsized("home/library unsized")],
            [ImageVariant.Unsized("home/library unsized")]);
    }

    private static ClientWarmupProfile CreateJellyfinWebProfile()
    {
        // Jellyfin Web computes fillWidth/fillHeight from viewport, card shape, and device pixel ratio.
        // These are common desktop/mobile/TV-ish variants rather than every possible browser size.
        var primaryVariants = new[]
        {
            ImageVariant.FillSize(213, 320, "library poster, desktop DPR1"),
            ImageVariant.FillSize(426, 640, "library poster, desktop DPR2"),
            ImageVariant.FillSize(223, 335, "home poster row, desktop DPR1"),
            ImageVariant.FillSize(446, 670, "home poster row, desktop DPR2"),
            ImageVariant.FillSize(298, 447, "home poster row, TV web mode"),
            ImageVariant.FillSize(596, 894, "home poster row, TV/high DPR"),
            ImageVariant.FillSize(320, 480, "library poster, TV web mode")
        };

        var thumbVariants = new[]
        {
            ImageVariant.FillSize(384, 216, "library thumb/backdrop, desktop DPR1"),
            ImageVariant.FillSize(768, 432, "library thumb/backdrop, desktop DPR2"),
            ImageVariant.FillSize(355, 200, "home thumb/backdrop row, desktop DPR1"),
            ImageVariant.FillSize(710, 400, "home thumb/backdrop row, desktop DPR2"),
            ImageVariant.FillSize(447, 251, "home thumb/backdrop row, TV web mode"),
            ImageVariant.FillSize(894, 502, "home thumb/backdrop row, TV/high DPR"),
            ImageVariant.FillSize(480, 270, "library thumb/backdrop, TV web mode")
        };

        return new ClientWarmupProfile("Jellyfin Web", "Viewport and DPR based fillWidth/fillHeight requests.", primaryVariants, thumbVariants);
    }

    private static ClientWarmupProfile CreateWebShellClientsProfile()
    {
        // WebShellClients means the native Android, iOS, and Desktop Qt apps when they display
        // Jellyfin's web UI inside their native shell. Their home/library image requests match Jellyfin Web.
        var webProfile = CreateJellyfinWebProfile();
        return new ClientWarmupProfile("WebShellClients", "Android/iOS/Desktop Qt web-shell clients; same home/library sizing behavior as Jellyfin Web.", webProfile.PrimaryVariants, webProfile.ThumbVariants);
    }

    private static ClientWarmupProfile CreateAndroidTvProfile()
    {
        var primaryVariants = new[]
        {
            ImageVariant.MaxSize(100, 150, "home poster"),
            ImageVariant.MaxSize(116, 174, "library poster smallest"),
            ImageVariant.MaxSize(118, 178, "library poster vertical smallest"),
            ImageVariant.MaxSize(136, 205, "library poster vertical small"),
            ImageVariant.MaxSize(144, 217, "library poster small"),
            ImageVariant.MaxSize(161, 242, "library poster vertical medium"),
            ImageVariant.MaxSize(192, 288, "library poster medium"),
            ImageVariant.MaxSize(252, 379, "library poster vertical large"),
            ImageVariant.MaxSize(284, 427, "library poster large"),
            ImageVariant.MaxSize(352, 528, "library poster x-large")
        };

        var thumbVariants = new[]
        {
            ImageVariant.MaxSize(267, 150, "home thumb/backdrop"),
            ImageVariant.MaxSize(161, 91, "library thumb vertical smallest"),
            ImageVariant.MaxSize(197, 111, "library thumb vertical small"),
            ImageVariant.MaxSize(222, 125, "library thumb smallest"),
            ImageVariant.MaxSize(252, 142, "library thumb vertical medium"),
            ImageVariant.MaxSize(259, 146, "library thumb small"),
            ImageVariant.MaxSize(309, 174, "library thumb medium"),
            ImageVariant.MaxSize(352, 198, "library thumb vertical large"),
            ImageVariant.MaxSize(385, 217, "library thumb large"),
            ImageVariant.MaxSize(581, 327, "library thumb vertical x-large"),
            ImageVariant.MaxSize(759, 427, "library thumb x-large")
        };

        return new ClientWarmupProfile("Android TV", "maxWidth/maxHeight requests for home rows and poster-size library settings.", primaryVariants, thumbVariants);
    }

    private static ClientWarmupProfile CreateRokuProfile()
    {
        var primaryVariants = new[]
        {
            ImageVariant.MaxSize(196, 384, "default poster helper"),
            ImageVariant.MaxSize(180, 331, "home movie poster display"),
            ImageVariant.MaxSize(295, 440, "library poster data"),
            ImageVariant.MaxSize(300, 450, "person/search style poster"),
            ImageVariant.MaxSize(464, 331, "home/library wide poster"),
            ImageVariant.MaxSize(400, 384, "episode row actual fallback"),
            ImageVariant.MaxSize(500, 500, "square audio/library art")
        };

        var thumbVariants = new[]
        {
            ImageVariant.MaxSize(196, 384, "default thumb fallback"),
            ImageVariant.MaxSize(295, 440, "portrait fallback thumb"),
            ImageVariant.MaxSize(400, 384, "episode row actual fallback"),
            ImageVariant.MaxSize(464, 331, "series/home landscape thumb"),
            ImageVariant.MaxSize(500, 500, "square thumb art")
        };

        return new ClientWarmupProfile("Roku", "Mostly fixed maxWidth/maxHeight requests used by Roku data nodes and home rows.", primaryVariants, thumbVariants);
    }

    private static ClientWarmupProfile CreateStreamyfinProfile()
    {
        var primaryVariants = new[]
        {
            ImageVariant.Width(1000, 90, "main library ItemImage"),
            ImageVariant.FillWidth(300, 80, "poster rows"),
            ImageVariant.FillHeight(389, 80, "episode poster / horizontal fallback")
        };

        var thumbVariants = new[]
        {
            ImageVariant.FillHeight(389, 80, "continue watching / next up horizontal cards")
        };

        return new ClientWarmupProfile("Streamyfin", "width/fillWidth/fillHeight requests used in library and home poster components.", primaryVariants, thumbVariants);
    }

    private sealed record ClientWarmupProfile(
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

    private sealed record ImageVariant(string Label, IReadOnlyList<KeyValuePair<string, string>> Query)
    {
        public string CacheKey => string.Join("&", Query.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase).Select(kvp => $"{kvp.Key}={kvp.Value}"));

        public static ImageVariant Unsized(string label)
        {
            return new ImageVariant(label, []);
        }

        public static ImageVariant MaxSize(int width, int height, string label, int? quality = null)
        {
            return Create(label, quality, ("maxWidth", width), ("maxHeight", height));
        }

        public static ImageVariant FillSize(int width, int height, string label, int? quality = 96)
        {
            return Create(label, quality, ("fillWidth", width), ("fillHeight", height));
        }

        public static ImageVariant FillWidth(int width, int quality, string label)
        {
            return Create(label, quality, ("fillWidth", width));
        }

        public static ImageVariant FillHeight(int height, int quality, string label)
        {
            return Create(label, quality, ("fillHeight", height));
        }

        public static ImageVariant Width(int width, int quality, string label)
        {
            return Create(label, quality, ("width", width));
        }

        private static ImageVariant Create(string label, int? quality, params (string Key, int Value)[] dimensions)
        {
            var query = dimensions
                .Select(dimension => new KeyValuePair<string, string>(dimension.Key, dimension.Value.ToString()))
                .ToList();

            if (quality.HasValue)
            {
                query.Add(new KeyValuePair<string, string>("quality", quality.Value.ToString()));
            }

            return new ImageVariant(label, query);
        }
    }

    private sealed record WarmupRequest(Guid ItemId, string ImageType, string ClientProfile, ImageVariant Variant)
    {
        public string CacheKey => $"{ItemId:N}:{ImageType}:{Variant.CacheKey}";

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

}
