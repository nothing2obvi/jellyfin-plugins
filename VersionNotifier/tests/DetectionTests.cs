using Jellyfin.Plugin.VersionNotifier;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using NSubstitute;
using Xunit;

namespace VersionNotifier.Tests;

public class DetectionTests
{
    private sealed class VisibleMovie : Movie { public bool Allowed = true; public override bool IsVisibleStandalone(User user) => Allowed; }
    private sealed class VisibleEpisode : Episode { public override bool IsVisibleStandalone(User user) => true; }
    private readonly ILibraryManager _library = Substitute.For<ILibraryManager>();
    private readonly IMediaSourceManager _sources = Substitute.For<IMediaSourceManager>();
    private readonly User _user = new("viewer", "default", "default");
    private AlternativeFinder Finder => new(_library, _sources);

    private T Add<T>(T item, params MediaSourceInfo[] sources) where T : BaseItem
    {
        _library.GetItemById(item.Id).Returns(item);
        _sources.GetStaticMediaSources(item, false, _user).Returns(sources);
        return item;
    }
    internal static MediaSourceInfo Source(Guid id, int width = 1920, int height = 1080) => new() {
        Id = id.ToString("N"), Path = "/media/file.mkv", SupportsDirectPlay = true,
        MediaStreams = [new() { Type = MediaStreamType.Video, Width = width, Height = height }]
    };
    private VisibleMovie Movie(string provider = "100") => new() { Id = Guid.NewGuid(), ProviderIds = new() { ["Tmdb"] = provider } };

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void GroupedVersionsUseSelectedSource(bool episode)
    {
        BaseItem a = episode ? new VisibleEpisode { Id = Guid.NewGuid() } : Movie();
        BaseItem b = episode ? new VisibleEpisode { Id = Guid.NewGuid() } : Movie();
        Add(b);
        Add(a, Source(a.Id), Source(b.Id, 3840, 1600));
        Assert.True(Finder.HasAlternative(a.Id, a.Id.ToString("N"), _user));
        Assert.False(Finder.HasAlternative(a.Id, b.Id.ToString("N"), _user));
        Assert.False(Finder.HasAlternative(a.Id, "unavailable", _user));
    }

    [Theory]
    [InlineData("100", true, true)]
    [InlineData("101", true, false)]
    [InlineData("100", false, false)]
    public void SeparateMoviesRequireIdentityAndPermission(string provider, bool allowed, bool expected)
    {
        var a = Movie(); var b = Movie(provider); b.Allowed = allowed;
        Add(a, Source(a.Id)); Add(b, Source(b.Id, 3840, 2160));
        _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([b]);
        Assert.Equal(expected, Finder.HasAlternative(a.Id, a.Id.ToString("N"), _user));
    }

    [Fact]
    public void GroupedRestrictedVersionIsExcluded()
    {
        var a = Movie(); var b = Movie(); b.Allowed = false;
        Add(b); Add(a, Source(a.Id), Source(b.Id, 3840, 2160));
        _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([]);
        Assert.False(Finder.HasAlternative(a.Id, a.Id.ToString("N"), _user));
    }

    [Theory]
    [InlineData(1, 2, true)] [InlineData(0, 2, false)] [InlineData(1, 3, false)]
    public void SeparateEpisodesRequireSameSeriesSeasonAndEpisode(int season, int number, bool expected)
    {
        var series = new Series { Id = Guid.NewGuid(), ProviderIds = new() { ["Tvdb"] = "10" } };
        var other = new Series { Id = Guid.NewGuid(), ProviderIds = new() { ["Tvdb"] = "10" } };
        var a = new VisibleEpisode { Id = Guid.NewGuid(), SeriesId = series.Id, ParentIndexNumber = 1, IndexNumber = 2 };
        var b = new VisibleEpisode { Id = Guid.NewGuid(), SeriesId = other.Id, ParentIndexNumber = season, IndexNumber = number };
        Add(series); Add(other); Add(a, Source(a.Id)); Add(b, Source(b.Id, 3840, 2160));
        _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(call =>
            ((InternalItemsQuery)call[0]).AncestorIds?.Length > 0 ? [b] : [other]);
        Assert.Equal(expected, Finder.HasAlternative(a.Id, a.Id.ToString("N"), _user));
    }

    [Theory]
    [InlineData("series")] [InlineData("order")] [InlineData("episode-id")]
    public void DifferentSeriesOrderOrEpisodeIdentityDoesNotMatch(string mismatch)
    {
        var series = new Series { Id = Guid.NewGuid(), ProviderIds = new() { ["Tvdb"] = "10" }, DisplayOrder = "aired" };
        var other = new Series { Id = Guid.NewGuid(), ProviderIds = new() { ["Tvdb"] = mismatch == "series" ? "11" : "10" }, DisplayOrder = mismatch == "order" ? "dvd" : "aired" };
        var a = new VisibleEpisode { Id = Guid.NewGuid(), SeriesId = series.Id, ParentIndexNumber = 1, IndexNumber = 2, ProviderIds = new() { ["Tvdb"] = "100" } };
        var b = new VisibleEpisode { Id = Guid.NewGuid(), SeriesId = other.Id, ParentIndexNumber = 1, IndexNumber = 2, ProviderIds = new() { ["TVDB"] = mismatch == "episode-id" ? "101" : "100" } };
        Add(series); Add(other); Add(a, Source(a.Id)); Add(b, Source(b.Id, 3840, 2160));
        _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(call =>
            ((InternalItemsQuery)call[0]).AncestorIds?.Length > 0 ? [b] : [other]);
        Assert.False(Finder.HasAlternative(a.Id, a.Id.ToString("N"), _user));
    }

    [Fact]
    public void LibraryChangesAndPermissionChangesAreNotCached()
    {
        var a = Movie(); var b = Movie(); Add(a, Source(a.Id)); Add(b, Source(b.Id, 3840, 2160));
        _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([b]);
        Assert.True(Finder.HasAlternative(a.Id, a.Id.ToString("N"), _user));
        b.Allowed = false;
        Assert.False(Finder.HasAlternative(a.Id, a.Id.ToString("N"), _user));
        b.Allowed = true;
        _sources.GetStaticMediaSources(b, false, _user).Returns([]);
        Assert.False(Finder.HasAlternative(a.Id, a.Id.ToString("N"), _user));
    }

    [Fact]
    public void UnknownResolutionAndNo4KAreQuiet()
    {
        var a = Movie(); Add(a, Source(a.Id, 0, 0));
        Assert.False(Finder.HasAlternative(a.Id, a.Id.ToString("N"), _user));
        Add(a, Source(a.Id));
        _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([]);
        Assert.False(Finder.HasAlternative(a.Id, a.Id.ToString("N"), _user));
    }

    [Fact]
    public void MissingMetadataAndNoAlternativeDoNotNotify()
    {
        var a = Movie(); a.ProviderIds.Clear(); Add(a, Source(a.Id));
        Assert.False(Finder.HasAlternative(a.Id, a.Id.ToString("N"), _user));
        _library.DidNotReceive().GetItemList(Arg.Any<InternalItemsQuery>());
    }

    [Fact]
    public void ConflictingProviderIdsAndMultiEpisodeRangesDoNotMatch()
    {
        Assert.False(Matching.SameIdentity(new Dictionary<string,string> { ["Tmdb"]="1", ["Imdb"]="tt1" },
            new Dictionary<string,string> { ["Tmdb"]="1", ["Imdb"]="tt2" }));
        Assert.False(Matching.SameEpisode(1,2,3,1,2,null));
        Assert.False(Matching.Is4K(Source(Guid.NewGuid(),2560,1440)));
    }
}
