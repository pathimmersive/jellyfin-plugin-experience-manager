using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ExperienceManager.Caching;
using Jellyfin.Plugin.ExperienceManager.Home;
using Jellyfin.Plugin.ExperienceManager.Layouts;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.ExperienceManager.Tests;

public class HomeCacheTests
{
    private readonly ILibraryManager _library = Substitute.For<ILibraryManager>();
    private readonly ITVSeriesManager _tv = Substitute.For<ITVSeriesManager>();
    private readonly IUserDataManager _userData = Substitute.For<IUserDataManager>();
    private readonly FakeTimeProvider _time = new();

    private static User NewUser(string name) => new(name, "auth", "reset") { Id = Guid.NewGuid() };

    private HomeCache Cache() => new(_library, _tv, _userData, _time);

    [Fact]
    public void LatestShowsAreTheSeriesOfTheNewestEpisodesFetchedInOneQuery()
    {
        var library = Guid.NewGuid();
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var episodes = new BaseItem[]
        {
            new Episode { SeriesId = b }, new Episode { SeriesId = b }, new Episode { SeriesId = a }, new Episode { SeriesId = c }, new Episode { SeriesId = a },
        };
        _library.GetItemList(Arg.Is<InternalItemsQuery>(q => q.IncludeItemTypes.Contains(BaseItemKind.Episode))).Returns(episodes);
        _library.GetItemList(Arg.Is<InternalItemsQuery>(q => q.IncludeItemTypes.Contains(BaseItemKind.Series)))
            .Returns(c => c.Arg<InternalItemsQuery>().ItemIds.Reverse().Select(id => (BaseItem)new Series { Id = id }).ToList());

        var shows = HomeCache.LatestShows(_library, library);

        Assert.Equal([b, a, c], shows.Select(s => s.Id));
        _library.Received(2).GetItemList(Arg.Any<InternalItemsQuery>());
        _library.DidNotReceiveWithAnyArgs().GetItemById(default);
        var episodeQuery = (InternalItemsQuery)_library.ReceivedCalls().First(c => c.GetMethodInfo().Name == nameof(ILibraryManager.GetItemList)).GetArguments()[0]!;
        Assert.Equal(library, episodeQuery.ParentId);
        Assert.Equal(HomeCache.RecentEpisodes, episodeQuery.Limit);
        Assert.Contains((ItemSortBy.DateCreated, Jellyfin.Database.Implementations.Enums.SortOrder.Descending), episodeQuery.OrderBy);
    }

    [Fact]
    public void EveryQuerySetsALimitAndSortsInTheDatabase()
    {
        _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([]);
        _tv.GetNextUp(Arg.Any<NextUpQuery>(), Arg.Any<DtoOptions>()).Returns(new QueryResult<BaseItem>());
        using var cache = Cache();
        var user = NewUser("viewer");

        cache.Featured(out _);
        cache.Latest(Guid.NewGuid(), CollectionType.movies, out _);
        cache.ContinueWatching(user, out _);
        cache.NextUp(user, out _);

        var queries = _library.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(ILibraryManager.GetItemList)).Select(c => (InternalItemsQuery)c.GetArguments()[0]!).ToList();
        Assert.Equal(3, queries.Count);
        Assert.All(queries, q =>
        {
            Assert.NotNull(q.Limit);
            Assert.NotEmpty(q.OrderBy);
        });
        _tv.Received(1).GetNextUp(Arg.Is<NextUpQuery>(q => q.Limit == HomeCache.UserLimit), Arg.Any<DtoOptions>());
    }

    [Fact]
    public void AUsersWatchDataChangeClearsOnlyThatUsersLists()
    {
        _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([]);
        _tv.GetNextUp(Arg.Any<NextUpQuery>(), Arg.Any<DtoOptions>()).Returns(new QueryResult<BaseItem>());
        using var cache = Cache();
        var (me, other) = (NewUser("me"), NewUser("other"));
        cache.ContinueWatching(me, out _);
        cache.NextUp(me, out _);
        cache.ContinueWatching(other, out _);

        _userData.UserDataSaved += Raise.EventWith(new UserDataSaveEventArgs { UserId = me.Id, Item = null!, UserData = null! });

        cache.ContinueWatching(me, out var resumeHit);
        cache.NextUp(me, out var nextUpHit);
        cache.ContinueWatching(other, out var otherHit);
        Assert.False(resumeHit);
        Assert.False(nextUpHit);
        Assert.True(otherHit);
    }

    [Fact]
    public void FeaturedCandidatesAreTheNewestFilmsWithABackdrop()
    {
        var withBackdrop = HomePageServiceTests.Film("with");
        withBackdrop.ImageInfos = [new MediaBrowser.Controller.Entities.ItemImageInfo { Type = MediaBrowser.Model.Entities.ImageType.Backdrop, Path = "/b.jpg" }];
        _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([HomePageServiceTests.Film("without"), withBackdrop]);
        using var cache = Cache();

        Assert.Equal([withBackdrop], cache.Featured(out _));
    }
}

public sealed class StoredPagesTests : IDisposable
{
    private readonly TempDatabase _db = new();
    private readonly ILibraryManager _library = Substitute.For<ILibraryManager>();
    private readonly IHomeSources _sources = Substitute.For<IHomeSources>();
    private readonly FakeTimeProvider _time = new();

    private static readonly User Viewer = new("viewer", "auth", "reset") { Id = Guid.NewGuid() };

    public void Dispose() => _db.Dispose();

    private StoredPages Pages(out HomeCache cache)
    {
        var tv = Substitute.For<ITVSeriesManager>();
        tv.GetNextUp(Arg.Any<NextUpQuery>(), Arg.Any<DtoOptions>()).Returns(new QueryResult<BaseItem>());
        cache = new HomeCache(_library, tv, Substitute.For<IUserDataManager>(), _time);
        return new StoredPages(_db.Store(), _db.Timeline(_time), cache, _sources, _time);
    }

    private IReadOnlyList<PageSection> Home(out HomeCache cache) => Pages(out cache).SectionsFor(PageIds.Home, Viewer);

    [Fact]
    public void AFreshHomeHoldsFeaturedMyMediaContinueWatchingNextUpThenLatestForEachMovieAndShowLibrary()
    {
        var (films, music, shows) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        _sources.GetViews(Viewer).Returns(
        [
            new LibraryView(films, "Movies", CollectionType.movies),
            new LibraryView(music, "Music", CollectionType.music),
            new LibraryView(shows, "Shows", CollectionType.tvshows),
        ]);

        var sections = Home(out var cache);
        using (cache)
        {
            Assert.Equal(["featured", "mymedia", "continue", "nextup", "latest-" + films.ToString("N"), "latest-" + shows.ToString("N")], sections.Select(s => s.Id));
            Assert.Equal("feature", sections[0].Display.Hint);
            Assert.Equal("Latest Shows", sections[5].Title);
            Assert.Equal("Series", sections[5].SeeAll!.IncludeItemTypes);
            Assert.Equal(films.ToString("N"), sections[4].SeeAll!.ParentId);
            Assert.Equal(
                [
                    new SectionSource(SectionSources.Featured),
                    new SectionSource(SectionSources.MyMedia),
                    new SectionSource(SectionSources.ContinueWatching),
                    new SectionSource(SectionSources.NextUp),
                    new SectionSource(SectionSources.Latest, films.ToString("N"), "Movie"),
                    new SectionSource(SectionSources.Latest, shows.ToString("N"), "Series"),
                ],
                sections.Select(s => s.Source));
        }
    }

    [Fact]
    public void MyMediaIsTheUsersLibrariesInTheirOwnOrder()
    {
        var (shows, films) = (new Folder { Id = Guid.NewGuid(), Name = "Shows" }, new Folder { Id = Guid.NewGuid(), Name = "Movies" });
        _sources.GetViews(Viewer).Returns([new LibraryView(shows.Id, "Shows", CollectionType.tvshows, shows), new LibraryView(films.Id, "Movies", CollectionType.movies, films)]);

        var sections = Home(out var cache);
        using (cache)
        {
            var myMedia = sections.Single(s => s.Id == "mymedia");
            Assert.Equal(1, sections.ToList().IndexOf(myMedia));
            Assert.Equal([shows, films], myMedia.Load(Viewer).Items);
            Assert.Equal("landscape", myMedia.Display.Shape);
        }
    }

    [Fact]
    public void EverySectionShowsOnlyWhatJellyfinWouldShowTheUser()
    {
        var films = Guid.NewGuid();
        var allowed = HomePageServiceTests.Film("allowed");
        var hidden = HomePageServiceTests.Film("hidden");
        _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([hidden, allowed]);
        _sources.GetViews(Viewer).Returns([new LibraryView(films, "Movies", CollectionType.movies)]);
        _sources.IsVisible(Arg.Any<BaseItem>(), Viewer).Returns(c => c.Arg<BaseItem>() != hidden);

        var sections = Home(out var cache);
        using (cache)
        {
            foreach (var section in sections)
            {
                Assert.DoesNotContain(hidden, section.Load(Viewer).Items);
            }

            Assert.Equal([allowed], sections.Single(s => s.Id.StartsWith("latest-", StringComparison.Ordinal)).Load(Viewer).Items);
        }
    }

    [Fact]
    public void ALibraryAddedLaterGetsItsLatestSectionWithoutAnEdit()
    {
        var films = Guid.NewGuid();
        _sources.GetViews(Viewer).Returns([new LibraryView(films, "Movies", CollectionType.movies)]);
        var pages = Pages(out var cache);
        using (cache)
        {
            Assert.Single(pages.SectionsFor(PageIds.Home, Viewer), s => s.Id.StartsWith("latest-", StringComparison.Ordinal));

            var shows = Guid.NewGuid();
            _sources.GetViews(Viewer).Returns([new LibraryView(films, "Movies", CollectionType.movies), new LibraryView(shows, "Shows", CollectionType.tvshows)]);

            Assert.Equal(["latest-" + films.ToString("N"), "latest-" + shows.ToString("N")], pages.SectionsFor(PageIds.Home, Viewer).Select(s => s.Id).Where(id => id.StartsWith("latest-", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void AnEditedHomeIsServedInItsNewOrderWithItsOwnTitlesAndANewLayoutVersion()
    {
        _sources.GetViews(Viewer).Returns([]);
        var store = _db.Store();
        var pages = new StoredPages(store, _db.Timeline(_time), new HomeCache(_library, Substitute.For<ITVSeriesManager>(), Substitute.For<IUserDataManager>(), _time), _sources, _time);
        var before = pages.LayoutVersion(PageIds.Home);

        store.Replace(PageIds.Home, [new("nextup", SectionTypes.NextUp, "Up next"), new("featured", SectionTypes.Featured)], out _);

        Assert.Equal(["nextup", "featured"], pages.SectionsFor(PageIds.Home, Viewer).Select(s => s.Id));
        Assert.Equal("Up next", pages.SectionsFor(PageIds.Home, Viewer)[0].Title);
        Assert.NotEqual(before, pages.LayoutVersion(PageIds.Home));
    }

    [Fact]
    public void APageThatIsNotStoredHasNoVersionAndNoSections()
    {
        var pages = Pages(out var cache);
        using (cache)
        {
            Assert.Null(pages.LayoutVersion("movies"));
            Assert.Empty(pages.SectionsFor("movies", Viewer));
        }
    }
}
