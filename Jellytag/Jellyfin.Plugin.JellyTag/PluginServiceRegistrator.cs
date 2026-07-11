using Jellyfin.Plugin.JellyTag.Middleware;
using Jellyfin.Plugin.JellyTag.Services;
using Jellyfin.Plugin.JellyTag.Tasks;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyTag;

/// <summary>
/// Registers plugin services with Jellyfin.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IQualityDetectionService, QualityDetectionService>();
        serviceCollection.AddSingleton<IImageOverlayService, ImageOverlayService>();
        serviceCollection.AddSingleton<IImageCacheService, ImageCacheService>();
        serviceCollection.AddSingleton<IImageTrafficCoordinator, ImageTrafficCoordinator>();
        serviceCollection.AddSingleton<ILearnedClientProfileService, LearnedClientProfileService>();
        serviceCollection.AddSingleton<IScheduledTask, CacheCleanupTask>();
        serviceCollection.AddSingleton<IScheduledTask, CacheWarmTask>();
        serviceCollection.AddSingleton<IScheduledTask, CalculateWarmerProgressTask>();
        serviceCollection.AddSingleton<IScheduledTask, BuildCollectionMembershipIndexTask>();
        serviceCollection.AddSingleton<IScheduledTask, ClearLearnedClientProfileTask>();

        // Register middleware via IStartupFilter to intercept image requests for ALL clients
        serviceCollection.AddSingleton<IStartupFilter, JellyTagStartupFilter>();
    }
}
