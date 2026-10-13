namespace Jellyfin.Plugin.ArtistPruner.Services;

public sealed class EmbeddedCredits
{
    // M4A and other ambiguous formats remain audit-only in this release.
    public static (string[] Artists, string[] AlbumArtists) Read(string path)
    {
        if (Path.GetExtension(path).ToLowerInvariant() is not (".flac" or ".mp3" or ".ogg" or ".opus"))
            throw new InvalidDataException("File format is not eligible for cleanup.");
        using var file = TagLib.File.Create(new ReadOnlyFile(path), TagLib.ReadStyle.None);
        var artists = file.Tag.Performers;
        var albumArtists = file.Tag.AlbumArtists;
        if (!CreditComparison.HasUsableCredits(artists) || !CreditComparison.HasUsableCredits(albumArtists))
            throw new InvalidDataException("Missing embedded artist or album artist tags.");
        return (artists, albumArtists);
    }
    private sealed class ReadOnlyFile(string path) : TagLib.File.IFileAbstraction
    {
        public string Name => path;
        public Stream ReadStream => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        public Stream WriteStream => throw new NotSupportedException("Music files are read-only.");
        public void CloseStream(Stream stream) => stream.Dispose();
    }
}
