using System.Text.Json;
namespace Jellyfin.Plugin.ArtistPruner.Services;

public sealed record PreviewRow(Guid ItemId, string Name, string Kind, string Field,
    string[] Before, string[] FileCredits, string[] Removed, string Status);
public sealed class PreviewReport
{
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedUtc { get; set; }
    public string Status { get; set; } = "Running";
    public bool DryRun { get; set; }
    public string Message { get; set; } = "";
    public List<PreviewRow> Rows { get; set; } = [];
}

/// <summary>Structured companion to this plugin's log messages; never parses other plugins' logs.</summary>
public sealed class PreviewStore(string path)
{
    private readonly object _sync = new();
    private PreviewReport? _current;
    public void Begin(bool dryRun) { lock (_sync) { _current = new() { DryRun = dryRun }; Save(); } }
    public void Add(PreviewRow row) { lock (_sync) { _current?.Rows.Add(row); } }
    public void Finish(string status, string message)
    {
        lock (_sync)
        {
            if (_current is null) return;
            _current.Status = status; _current.Message = message; _current.FinishedUtc = DateTime.UtcNow; Save();
        }
    }
    public PreviewReport? Read()
    {
        lock (_sync)
        {
            var report = _current is null ? (File.Exists(path) ? JsonSerializer.Deserialize<PreviewReport>(File.ReadAllText(path)) : null)
                : JsonSerializer.Deserialize<PreviewReport>(JsonSerializer.Serialize(_current));
            if (_current is null && report?.Status == "Running")
            { report.Status = "Interrupted"; report.Message = "The server stopped before this run completed; results are incomplete."; }
            return report;
        }
    }
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_current));
        File.Move(temp, path, true);
    }
}
