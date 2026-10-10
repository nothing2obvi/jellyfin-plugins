using Jellyfin.Data.Enums;

using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.VersionNotifier;

public sealed class AlternativeFinder(ILibraryManager library, IMediaSourceManager sources)
{
    public bool HasAlternative(Guid itemId, string sourceId, User user)
    {
        var item = library.GetItemById(itemId);
        if (item is not Movie && item is not Episode || item is null || !item.IsVisibleStandalone(user)) return false;
        var grouped = AccessibleSources(item, user).ToArray();
        var playing = string.IsNullOrWhiteSpace(sourceId)
            ? (grouped.Length == 1 ? grouped[0] : null)
            : grouped.FirstOrDefault(s => string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase));
        // Missing source IDs are safe only when exactly one accessible source exists.
        if (playing is null || !Matching.HasResolution(playing) || Matching.Is4K(playing)) return false;
        if (grouped.Any(Matching.Is4K)) return true;

        IEnumerable<BaseItem> candidates;
        if (item is Movie)
        {
            var ids = Matching.ReliableIds(item.ProviderIds);
            if (ids.Count == 0) return false;
            candidates = library.GetItemList(new InternalItemsQuery(user) {
                IncludeItemTypes = [BaseItemKind.Movie], Recursive = true, IsVirtualItem = false,
                HasAnyProviderId = ids, EnableTotalRecordCount = false
            }).Where(c => Matching.SameIdentity(item.ProviderIds, c.ProviderIds));
        }
        else
        {
            var episode = (Episode)item;
            var series = library.GetItemById(episode.SeriesId);
            if (series is null || !episode.ParentIndexNumber.HasValue || !episode.IndexNumber.HasValue) return false;
            var ids = Matching.ReliableIds(series.ProviderIds);
            if (ids.Count == 0) return false;
            var seriesIds = library.GetItemList(new InternalItemsQuery(user) {
                IncludeItemTypes = [BaseItemKind.Series], Recursive = true, HasAnyProviderId = ids,
                EnableTotalRecordCount = false
            }).Where(s => Matching.SameIdentity(series.ProviderIds, s.ProviderIds)
                && s is Series candidateSeries && series is Series originalSeries
                && string.Equals(originalSeries.DisplayOrder ?? "", candidateSeries.DisplayOrder ?? "", StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Id).ToArray();
            if (seriesIds.Length == 0) return false;
            candidates = library.GetItemList(new InternalItemsQuery(user) {
                IncludeItemTypes = [BaseItemKind.Episode], Recursive = true, AncestorIds = seriesIds,
                ParentIndexNumber = episode.ParentIndexNumber, IndexNumber = episode.IndexNumber,
                IsVirtualItem = false, EnableTotalRecordCount = false
            }).OfType<Episode>().Where(e => Matching.SameEpisode(episode.ParentIndexNumber, episode.IndexNumber,
                episode.IndexNumberEnd, e.ParentIndexNumber, e.IndexNumber, e.IndexNumberEnd)
                && !ConflictingEpisodeIds(episode, e));
        }
        return candidates.Any(c => c.Id != item.Id && c.IsVisibleStandalone(user) && AccessibleSources(c, user).Any(Matching.Is4K));
    }

    private static bool ConflictingEpisodeIds(Episode a, Episode b)
    {
        var left = Matching.ReliableIds(a.ProviderIds);
        var right = Matching.ReliableIds(b.ProviderIds);
        return left.Keys.Intersect(right.Keys, StringComparer.OrdinalIgnoreCase).Any(k => !string.Equals(left[k], right[k], StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<MediaSourceInfo> AccessibleSources(BaseItem item, User user) =>
        sources.GetStaticMediaSources(item, false, user).Where(s =>
            !string.IsNullOrEmpty(s.Path) &&
            Guid.TryParse(s.Id, out var id) && library.GetItemById(id) is { } version && version.IsVisibleStandalone(user));
}
