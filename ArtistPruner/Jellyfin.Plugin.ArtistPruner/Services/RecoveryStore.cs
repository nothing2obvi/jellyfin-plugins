using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.ArtistPruner.Models;

namespace Jellyfin.Plugin.ArtistPruner.Services;

/// <summary>Atomic, checksummed recovery journal. Unrestored points are never expired.</summary>
public sealed class RecoveryStore
{
    private readonly string _directory;
    public RecoveryStore(string directory) => _directory = directory;
    private sealed record Envelope(string Payload, string Sha256);
    private string FileName(Guid id) => Path.Combine(_directory, id.ToString("N") + ".json");

    public void Save(RecoveryPoint point, bool create = false)
    {
        Validate(point);
        Directory.CreateDirectory(_directory);
        var payload = JsonSerializer.Serialize(point);
        var envelope = JsonSerializer.Serialize(new Envelope(payload, Hash(payload)));
        if (Encoding.UTF8.GetByteCount(envelope) > 16 * 1024 * 1024)
            throw new InvalidDataException("Recovery point exceeds 16 MiB; no changes may be applied.");
        var temp = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(envelope);
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temp, FileName(point.Id), !create);
            var verified = Load(point.Id);
            if (JsonSerializer.Serialize(verified) != payload)
                throw new InvalidDataException("Recovery point read-back mismatch.");
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public RecoveryPoint Load(Guid id)
    {
        var path = FileName(id);
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Oversized recovery point.");
        var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Invalid recovery envelope.");
        if (envelope.Payload is null || envelope.Sha256 != Hash(envelope.Payload))
            throw new InvalidDataException("Recovery point checksum failed.");
        var point = JsonSerializer.Deserialize<RecoveryPoint>(envelope.Payload)
            ?? throw new InvalidDataException("Invalid recovery payload.");
        Validate(point);
        if (point.Id != id) throw new InvalidDataException("Recovery point ID mismatch.");
        return point;
    }

    // Fail closed on corruption: it may contain artist protections needed by other points.
    public List<RecoveryPoint> List() => !Directory.Exists(_directory) ? [] :
        Directory.EnumerateFiles(_directory, "*.json")
            .Select(path => Load(Guid.ParseExact(Path.GetFileNameWithoutExtension(path), "N")))
            .OrderByDescending(point => point.CreatedUtc).ToList();

    public void Retain(int count)
    {
        foreach (var point in List().Where(p => p.State == "Restored").Skip(Math.Clamp(count, 1, 100)))
            File.Delete(FileName(point.Id));
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void Validate(RecoveryPoint point)
    {
        if (point.Schema != 1 || point.Id == Guid.Empty || point.CreatedUtc == default
            || point.State is not ("Prepared" or "Applied" or "Restored")
            || point.Changes is null || point.Changes.Count == 0
            || point.Changes.Select(c => c.Id).Distinct().Count() != point.Changes.Count)
            throw new InvalidDataException("Invalid recovery point schema or state.");
        foreach (var change in point.Changes)
        {
            if (change.Id == Guid.Empty || string.IsNullOrWhiteSpace(change.Path)
                || change.Before is null || change.After is null || change.Artists is null
                || change.ProviderIds is null || change.TrackIds is null || change.ProtectedArtists is null
                || change.After.Length == 0 || change.Before.Length <= change.After.Length
                || change.Before[0] != change.After[0] || change.ProtectedArtists.Count == 0
                || change.Before.Concat(change.After).Any(string.IsNullOrWhiteSpace)
                || change.After.Except(change.Before, StringComparer.Ordinal).Any()
                || change.ProtectedArtists.Any(a => a.Id == Guid.Empty || a.Name is null || a.ProviderIds is null))
                throw new InvalidDataException("Invalid album recovery data.");
        }
    }
}
