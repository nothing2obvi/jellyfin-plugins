using System.Collections.Generic;
using Jellyfin.Plugin.ArtistPruner.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ArtistPruner;

/// <summary>
/// Main plugin class for music artist cleanup.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Unique plugin identifier.
    /// </summary>
    public static readonly Guid PluginId = new("e8b5f1a3-7c9d-4e2b-8a1f-3d6c5e4b9a0f");

    /// <summary>
    /// Initializes a new instance of the plugin.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public static Plugin? Instance { get; private set; }

    public override string Name => "Artist Pruner";

    /// <inheritdoc />
    public override string Description => "Preview and reversibly correct extra artist credits on tracks and albums using embedded credits.";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <summary>
    /// Returns the plugin's web pages.
    /// </summary>
    /// <returns>Collection of web pages.</returns>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            }
        };
    }
}
