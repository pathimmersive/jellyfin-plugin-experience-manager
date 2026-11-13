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
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.ExperienceManager.Tests;

public sealed class PlayedRowsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly User _ann = new("ann", "auth", "reset") { Id = Guid.NewGuid() };
    private readonly User _ben = new("ben", "auth", "reset") { Id = Guid.NewGuid() };
    private readonly User _cat = new("cat", "auth", "reset") { Id = Guid.NewGuid() };
    private readonly FakeTimeProvider _time = new(Now);
    private readonly ILibraryManager _library = Substitute.For<ILibraryManager>();
    private readonly IUserDataManager _userData = Substitute.For<IUserDataManager>();
    private readonly IUserManager _users = Substitute.For<IUserManager>();
    private readonly IHomeSources _sources = Substitute.For<IHomeSources>();
    private readonly Movie _a = Film("A");
    private readonly Movie _b = Film("B");
    private readonly Movie _c = Film("C");
    private readonly Series _show = new() { Id = Guid.NewGuid(), Name = "S", ParentId = Guid.NewGuid() };
    private readonly Dictionary<Guid, List<(BaseItem Item, UserItemData Data)>> _plays = new();
    private readonly Dictionary<Guid, IReadOnlyList<BaseItem>> _children = new();
    private readonly List<BaseItem> _boxes = [];
    private readonly TempDatabase _db = new();
    private PlayedRows? _rows;

    public PlayedRowsTests()
    {
        _users.GetUsers().Returns([_ann, _ben, _cat]);
        _library.GetItemById(_show.Id).Returns(_show);
        _sources.IsVisible(Arg.Any<BaseItem>(), Arg.Any<User>()).Returns(true);
        _sources.GetViews(Arg.Any<User>()).Returns([]);
        _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(c =>
        {
            var query = c.Arg<InternalItemsQuery>();
            if (query.IncludeItemTypes.Contains(BaseItemKind.BoxSet))
            {
                return _boxes;
            }

            return _plays.TryGetValue(query.User!.Id, out var played) ? played.OrderByDescending(p => p.Data.LastPlayedDate).Select(p => p.Item).ToList() : [];
        });
        foreach (var user in new[] { _ann, _ben, _cat })
        {
            _userData.GetUserDataBatch(Arg.Any<IReadOnlyList<BaseItem>>(), user).Returns(c =>
            {
                var asked = c.Arg<IReadOnlyList<BaseItem>>().Select(i => i.Id).ToHashSet();
                return (_plays.GetValueOrDefault(user.Id) ?? []).Where(p => asked.Contains(p.Item.Id)).ToDictionary(p => p.Item.Id, p => p.Data);
            });
        }
    }

    public void Dispose()
    {
        _rows?.Dispose();
        _db.Dispose();
    }

    private static Movie Film(string name) => new() { Id = Guid.NewGuid(), Name = name, ParentId = Guid.NewGuid() };

    private void Played(User user, BaseItem item, double daysAgo, int times = 1, bool finished = true)
    {
        if (!_plays.TryGetValue(user.Id, out var list))
        {
            _plays[user.Id] = list = [];
        }

        list.Add((item, new UserItemData { Key = item.Id.ToString("N"), Played = finished, PlayCount = times, LastPlayedDate = Now.UtcDateTime.AddDays(-daysAgo) }));
    }

    private Episode EpisodeOf(Series show) => new() { Id = Guid.NewGuid(), Name = "e", SeriesId = show.Id, ParentId = Guid.NewGuid() };

    private PlayedRows Rows() => _rows = new PlayedRows(_library, _userData, _users, _sources, _time, f => _children.GetValueOrDefault(f.Id) ?? []);

    [Fact]
    public void TopFilmsCountsDifferentUsersInTheLastSevenDaysNotPlays()
    {
        Played(_ann, _a, 1);
        Played(_ben, _a, 2);
        Played(_cat, _a, 3);
        Played(_ann, _b, 1, times: 9);
        Played(_ben, _b, 6.9);
        Played(_ann, _c, 8);
        Played(_ben, _c, 8);
        Played(_cat, _c, 8);

        Assert.Equal(["A", "B"], Rows().TopFilms(out _).Select(i => i.Name));
    }

    [Fact]
    public void AnEpisodeCountsForItsShowOncePerUser()
    {
        Played(_ann, EpisodeOf(_show), 1);
        Played(_ann, EpisodeOf(_show), 2, times: 4);
        Played(_ben, EpisodeOf(_show), 3);

        var shows = Rows().TopShows(out _);

        Assert.Same(_show, Assert.Single(shows));
        Assert.Empty(Rows().TopFilms(out _));
    }

    [Fact]
    public void TiesGoToTheMostRecentlyPlayed()
    {
        Played(_ann, _a, 3);
        Played(_ben, _b, 1);
        Assert.Equal(["B", "A"], Rows().TopFilms(out _).Select(i => i.Name));
    }

    [Fact]
    public void FinishTheseCollectionsHasStartedOnesMostRecentFirstWithTheirChips()
    {
        var started = new BoxSet { Id = Guid.NewGuid(), Name = "Started", ParentId = Guid.NewGuid() };
        var recent = new BoxSet { Id = Guid.NewGuid(), Name = "Recent", ParentId = Guid.NewGuid() };
        var done = new BoxSet { Id = Guid.NewGuid(), Name = "Done", ParentId = Guid.NewGuid() };
        var fresh = new BoxSet { Id = Guid.NewGuid(), Name = "Fresh", ParentId = Guid.NewGuid() };
        _boxes.AddRange([started, recent, done, fresh]);
        var films = Enumerable.Range(0, 9).Select(i => Film("f" + i)).ToArray();
        _children[started.Id] = [films[0], films[1], films[2]];
        _children[recent.Id] = [films[3], films[4]];
        _children[done.Id] = [films[5], films[6]];
        _children[fresh.Id] = [films[7], films[8]];
        Played(_ann, films[0], 20);
        Played(_ann, films[1], 10);
        Played(_ann, films[3], 1);
        Played(_ann, films[4], 30, finished: false);
        Played(_ann, films[5], 5);
        Played(_ann, films[6], 5);

        var rows = Rows();

        Assert.Equal(["Recent", "Started"], rows.FinishCollections(_ann, out _).Select(b => b.Name));
        Assert.Equal("2 of 3 watched", rows.FinishChips(_ann)[started.Id]);
        Assert.Equal("1 of 2 watched", rows.FinishChips(_ann)[recent.Id]);
        Assert.Empty(rows.FinishCollections(_ben, out _));
    }

    [Fact]
    public void HomeServesTop10RankedForWhatTheViewerMaySeeAndFinishWithChips()
    {
        var films = Enumerable.Range(0, 12).Select(i => Film("t" + i)).ToArray();
        for (var i = 0; i < films.Length; i++)
        {
            Played(_ann, films[i], 0.1 + (i * 0.5));
        }

        _sources.IsVisible(films[0], _ben).Returns(false);
        var box = new BoxSet { Id = Guid.NewGuid(), Name = "Box", ParentId = Guid.NewGuid() };
        _boxes.Add(box);
        _children[box.Id] = [films[0], films[1]];
        _plays[_ben.Id] = [(films[2], new UserItemData { Key = "k", Played = true, LastPlayedDate = Now.UtcDateTime })];
        _children[box.Id] = [films[2], films[3]];

        var pages = _db.Store();
        Assert.NotNull(pages.Replace(PageIds.Home, [new StoredSection("top", SectionTypes.TopFilms, null, null), new StoredSection("finish", SectionTypes.FinishCollections, null, null)], out _));
        var tv = Substitute.For<ITVSeriesManager>();
        tv.GetNextUp(Arg.Any<NextUpQuery>(), Arg.Any<DtoOptions>()).Returns(new QueryResult<BaseItem>());
        using var cache = new HomeCache(_library, tv, _userData, _time);
        using var timeline = _db.Timeline(_time);
        var stored = new StoredPages(pages, timeline, cache, _sources, _time, played: Rows());
        var service = new HomePageService(stored, Substitute.For<IDtoService>(), NullLogger<HomePageService>.Instance);

        var sections = stored.SectionsFor(PageIds.Home, _ben, 0);
        var top = sections.Single(s => s.Id == "top");
        Assert.True(top.Rank);
        // t2 has two viewers; the rest one each, most recent first; t0 is hidden from ben; ten at most.
        Assert.Equal(["t2", "t1", "t3", "t4", "t5", "t6", "t7", "t8", "t9", "t10"], top.Load(_ben).Items.Select(i => i.Name));

        var page = service.BuildPage(_ben, PageIds.Home, 0, ItemShape.Default, 0, out _)!;
        Assert.Equal((true, "Top 10 Films"), (page.Sections[0].Rank, page.Sections[0].Title));
        Assert.Equal(new Dictionary<string, string> { [box.Id.ToString("N")] = "1 of 2 watched" }, page.Sections[1].Chips);
        Assert.Null(page.Sections[0].Chips);
    }
}
