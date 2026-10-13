namespace Jellyfin.Plugin.ArtistPruner.Services;

/// <summary>Compare credits without transliteration, case folding or Unicode normalization.</summary>
public static class CreditComparison
{
    public static bool SameCredits(IEnumerable<string> stored, IEnumerable<string> embedded) =>
        new HashSet<string>(stored, StringComparer.Ordinal).SetEquals(embedded);

    public static bool HasUsableCredits(IEnumerable<string> values) =>
        values.Any() && values.All(value => !string.IsNullOrWhiteSpace(value));
}
