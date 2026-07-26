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

    /// <summary>
    /// Gets or sets a value indicating whether this task should be ignored by conflict detection.
    /// </summary>
    public bool IgnoreConflicts { get; set; }
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
    /// Gets or sets a value indicating whether tasks without daily or weekly triggers should appear in the task list.
    /// </summary>
    public bool ShowUnscheduledTasks { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether overlapping scheduled tasks should be highlighted.
    /// </summary>
    public bool ShowConflictHighlights { get; set; }

    /// <summary>
    /// Gets or sets the grid zoom percentage.
    /// </summary>
    public int GridZoomPercent { get; set; } = 100;

    /// <summary>
    /// Gets or sets recently used task colors.
    /// </summary>
    public List<string> RecentColors { get; set; } = new();
}
