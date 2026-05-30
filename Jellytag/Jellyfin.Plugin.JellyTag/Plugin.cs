using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyTag.Configuration;
using Jellyfin.Plugin.JellyTag.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.JellyTag;

/// <summary>
/// JellyTag-Plus plugin - Adds quality badges to media posters.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Gets the plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // Run legacy migration once at startup
        Configuration.MigrateFromLegacy();
        if (WarmerClientProfileSettingsStore.Apply(Configuration, WarmerClientProfileSettingsStore.Load(DataFolderPath)))
        {
            UpdateConfiguration(Configuration);
        }
    }

    /// <inheritdoc />
    public override string Name => "JellyTag-Plus";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("a3db8d87-9a5a-4f35-94b5-7df409f7dc01");

    /// <inheritdoc />
    public override string Description => "Adds quality badges (4K, 1080p, etc.) to media posters and thumbnails.";

    /// <summary>
    /// Gets the cache folder path for storing processed images.
    /// </summary>
    public string CacheFolderPath => Path.Combine(DataFolderPath, "cache");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
                EnableInMainMenu = true,
                MenuSection = "Extensions",
                MenuIcon = "style"
            }
        };
    }
}
