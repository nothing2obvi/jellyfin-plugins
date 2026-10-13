namespace Jellyfin.Plugin.ArtistPruner.Models;

public sealed class PruneResult
{
    public int TracksInspected { get; set; }
    public int TracksSkipped { get; set; }
    public int CreditMismatches { get; set; }
    public int AlbumsInspected { get; set; }
    public int AlbumsSkipped { get; set; }
    public int AlbumCreditMismatches { get; set; }
    public int ArtistsInspected { get; set; }
    public int UnsupportedNames { get; set; }
    public int EntriesSkipped { get; set; }
    public bool WasDryRun { get; set; } = true;
    public int ChangesProposed { get; set; }
    public int AssociationsCorrected { get; set; }
    public int EntriesRemoved => 0;
    public string Summary { get; set; } = string.Empty;
}
