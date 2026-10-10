using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.VersionNotifier;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public const string DefaultMessage = "[Auto] This title is also available in 4K.";
    public bool Enabled { get; set; } = true;
    public int DelaySeconds { get; set; } = 15;
    public string Message { get; set; } = DefaultMessage;
    public int EffectiveDelay => Math.Clamp(DelaySeconds, 0, 300);
    public string EffectiveMessage => string.IsNullOrWhiteSpace(Message) ? DefaultMessage : Message.Trim();
}

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) => Instance = this;
    public static Plugin? Instance { get; private set; }
    public override Guid Id => new("c5ef5e31-b28c-4cfb-9e53-61bb01819a72");
    public override string Name => "Version Notifier";
    public override string Description => "Notifies a playback session when an accessible 4K alternative exists.";
    public IEnumerable<PluginPageInfo> GetPages() => [new() { Name = "VersionNotifier", EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html" }];
}
