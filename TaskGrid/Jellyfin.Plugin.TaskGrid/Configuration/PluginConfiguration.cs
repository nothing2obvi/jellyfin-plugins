using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TaskGrid.Configuration;

/// <summary>
/// Saved display preferences for one scheduled task.
/// </summary>
public class TaskGridStyle
{
    /// <summary>
    /// Gets or sets the Jellyfin task key, falling back to the task id when a key is unavailable.
    /// </summary>
    public string TaskKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the color used for the task block.
    /// </summary>
    public string Color { get; set; } = "#00a4dc";

    /// <summary>
    /// Gets or sets a value indicating whether this task should be treated as heavy for overlap warnings.
    /// </summary>
    public bool Heavy { get; set; }
}

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the task display preferences.
    /// </summary>
    public List<TaskGridStyle> TaskStyles { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether hidden scheduled tasks should appear in the grid.
    /// </summary>
    public bool ShowHiddenTasks { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether tasks without daily or weekly triggers should appear below the grid.
    /// </summary>
    public bool ShowUnscheduledTasks { get; set; } = true;
}
