namespace Jellyfin.Plugin.ArtistPruner.Models;

public sealed class ArtistProtection
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public Guid ParentId { get; set; }
    public bool WasLocked { get; set; }
    public Dictionary<string, string> ProviderIds { get; set; } = [];
}

public sealed class AlbumChange
{
    public string Kind { get; set; } = "MusicAlbum";
    public string[]? AfterArtists { get; set; }
    public Dictionary<Guid, string[]> Evidence { get; set; } = [];
    public Guid Id { get; set; }
    public string Path { get; set; } = "";
    public Guid ParentId { get; set; }
    public string[] Before { get; set; } = [];
    public string[] After { get; set; } = [];
    public string[] Artists { get; set; } = [];
    public Guid[] TrackIds { get; set; } = [];
    public Dictionary<string, string> ProviderIds { get; set; } = [];
    public List<ArtistProtection> ProtectedArtists { get; set; } = [];
}

public sealed class RecoveryPoint
{
    public int Schema { get; set; } = 2;
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    // Prepared is durable BEFORE any mutation. Restore accepts Prepared after a crash.
    public string State { get; set; } = "Prepared";
    public List<AlbumChange> Changes { get; set; } = [];
}
