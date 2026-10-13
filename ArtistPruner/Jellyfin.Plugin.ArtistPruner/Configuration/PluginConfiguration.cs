using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ArtistPruner.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;
    public bool DryRun { get; set; } = true;
    public int BackupRetentionCount { get; set; } = 5;
    public bool AutoCleanAfterLibraryScan { get; set; }
}
