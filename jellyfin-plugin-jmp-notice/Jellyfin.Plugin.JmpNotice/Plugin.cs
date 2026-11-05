using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.JmpNotice;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public const string DefaultMessage = "Jellyfin Media Player has been archived. Please see the Discord announcement for better alternatives.";
    public bool Enabled { get; set; } = true;
    public int DurationSeconds { get; set; } = 12;
    public string Message { get; set; } = DefaultMessage;
    public int EffectiveDuration => Math.Clamp(DurationSeconds, 1, 60);
    public string EffectiveMessage => string.IsNullOrWhiteSpace(Message) ? DefaultMessage : Message.Trim();
}

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) => Instance = this;
    public static Plugin? Instance { get; private set; }
    public override Guid Id => new("603574cd-910d-462b-a51b-d518dd05b0e1");
    public override string Name => "JMP Notice";
    public override string Description => "Shows a migration notice to Jellyfin Media Player sessions.";
    public IEnumerable<PluginPageInfo> GetPages() => [new() { Name = "JmpNotice", EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html" }];
}
