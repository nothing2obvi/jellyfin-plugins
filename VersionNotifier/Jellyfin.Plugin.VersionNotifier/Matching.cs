using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.VersionNotifier;

public static class Matching
{
    // Width catches cropped UHD / DCI releases without calling 1440p '4K'.
    public static bool Is4K(MediaSourceInfo source) => source.MediaStreams.Any(s =>
        s.Type == MediaStreamType.Video && (s.Width >= 3800 || s.Height >= 2160));
    public static bool HasResolution(MediaSourceInfo source) => source.MediaStreams.Any(s =>
        s.Type == MediaStreamType.Video && s.Width > 0 && s.Height > 0);

    public static Dictionary<string, string> ReliableIds(IReadOnlyDictionary<string, string> ids) => ids
        .Where(p => new[] { "Tmdb", "Imdb", "Tvdb" }.Contains(p.Key, StringComparer.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(p.Value))
        .ToDictionary(p => p.Key, p => p.Value.Trim(), StringComparer.OrdinalIgnoreCase);

    // One shared ID is required; conflicting IDs veto a match, even if another agrees.
    public static bool SameIdentity(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right)
    {
        var a = ReliableIds(left);
        var b = ReliableIds(right);
        var shared = a.Keys.Intersect(b.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
        return shared.Length > 0 && shared.All(k => string.Equals(a[k], b[k], StringComparison.OrdinalIgnoreCase));
    }

    public static bool SameEpisode(int? season, int? episode, int? end, int? otherSeason, int? otherEpisode, int? otherEnd) =>
        season.HasValue && episode.HasValue && season == otherSeason && episode == otherEpisode
        && (end ?? episode) == (otherEnd ?? otherEpisode);
}
