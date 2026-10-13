using Jellyfin.Plugin.ArtistPruner.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArtistPruner.Services;

/// <summary>Queue the native task after a successful full library scan.</summary>
public sealed class ScanCompletedService(ITaskManager tasks, ILibraryManager library,
    ILogger<ScanCompletedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        tasks.TaskCompleted += OnTaskCompleted;
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken)
    {
        tasks.TaskCompleted -= OnTaskCompleted;
        return Task.CompletedTask;
    }
    private void OnTaskCompleted(object? sender, TaskCompletionEventArgs args)
    {
        if (args.Task.ScheduledTask.Key != "RefreshLibrary" || args.Result.Status != TaskCompletionStatus.Completed)
            return;
        var config = Plugin.Instance?.Configuration;
        if (config is not { Enabled: true, AutoCleanAfterLibraryScan: true }) return;
        try
        {
            if (library.GetVirtualFolders().Any(folder => folder.CollectionType == CollectionTypeOptions.music))
                tasks.QueueIfNotRunning<PruneArtistsTask>();
        }
        catch (Exception ex) { logger.LogError(ex, "Could not queue Artist Pruner after the library scan"); }
    }
}
