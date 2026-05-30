using System.Text.Json;
using Jellyfin.Plugin.JellyTag.Configuration;

namespace Jellyfin.Plugin.JellyTag.Services;

public static class WarmerClientProfileSettingsStore
{
    private const string FileName = "warmer-client-profiles.json";

    public static WarmerClientProfileSettings? Load(string dataFolderPath)
    {
        var path = GetPath(dataFolderPath);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<WarmerClientProfileSettings>(json);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string dataFolderPath, PluginConfiguration config)
    {
        var settings = new WarmerClientProfileSettings
        {
            Profiles = new List<string>(config.WarmerClientProfiles ?? []),
            Order = new List<string>(config.WarmerClientProfileOrder ?? [])
        };

        Directory.CreateDirectory(dataFolderPath);
        var path = GetPath(dataFolderPath);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    public static void Delete(string dataFolderPath)
    {
        TryDelete(GetPath(dataFolderPath));
    }

    public static bool Apply(PluginConfiguration config, WarmerClientProfileSettings? settings)
    {
        if (settings?.Profiles == null || settings.Order == null)
        {
            return false;
        }

        if (ListsEqual(config.WarmerClientProfiles, settings.Profiles)
            && ListsEqual(config.WarmerClientProfileOrder, settings.Order))
        {
            return false;
        }

        config.WarmerClientProfiles = new List<string>(settings.Profiles);
        config.WarmerClientProfileOrder = new List<string>(settings.Order);
        return true;
    }

    private static string GetPath(string dataFolderPath)
    {
        return Path.Combine(dataFolderPath, FileName);
    }

    private static bool ListsEqual(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        if (left == null || right == null)
        {
            return left == right;
        }

        return left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}

public sealed class WarmerClientProfileSettings
{
    public List<string> Profiles { get; set; } = [];
    public List<string> Order { get; set; } = [];
}
