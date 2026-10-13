using Jellyfin.Plugin.ArtistPruner.Services;
using Jellyfin.Plugin.ArtistPruner.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ArtistPruner;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton(_ => new RecoveryStore(Path.Combine(
            Plugin.Instance?.DataFolderPath ?? throw new InvalidOperationException("Plugin not initialized."), "backups")));
        services.AddSingleton(_ => new PreviewStore(Path.Combine(Plugin.Instance!.DataFolderPath, "preview.json")));
        services.AddSingleton<CleanupService>();
        services.AddSingleton<IArtistPruneService, ArtistPruneService>();
        services.AddHostedService<ScanCompletedService>();
        // Jellyfin discovers IScheduledTask implementations from the plugin assembly.
    }
}
