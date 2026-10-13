namespace Jellyfin.Plugin.ArtistPruner.Services;

public sealed class EmbeddedCredits
{
    // M4A and other ambiguous formats remain audit-only in this release.
    public static (string[] Artists, string[] AlbumArtists) Read(string path)
    {
        if (Path.GetExtension(path).ToLowerInvariant() is not (".flac" or ".mp3" or ".ogg" or ".opus"))
            throw new InvalidDataException("File format is not eligible for cleanup.");
        using var file = TagLib.File.Create(new ReadOnlyFile(path), TagLib.ReadStyle.None);
        // Alternate plural tags can carry additional performers that the default accessor
        // does not expose. Skip rather than mistake those credits for unwanted artists.
        if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph
            && (xiph.GetField("ARTISTS").Length > 0 || xiph.GetField("ALBUMARTISTS").Length > 0))
            throw new InvalidDataException("Plural ARTISTS/ALBUMARTISTS tags require manual review.");
        if (file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3
            && (TagLib.Id3v2.UserTextInformationFrame.Get(id3, "ARTISTS", false) is not null
                || TagLib.Id3v2.UserTextInformationFrame.Get(id3, "ALBUMARTISTS", false) is not null))
            throw new InvalidDataException("Plural ARTISTS/ALBUMARTISTS tags require manual review.");
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
