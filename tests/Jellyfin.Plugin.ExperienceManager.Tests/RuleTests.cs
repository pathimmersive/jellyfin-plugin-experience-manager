using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ExperienceManager.Audiences;
using Jellyfin.Plugin.ExperienceManager.Controllers;
using Jellyfin.Plugin.ExperienceManager.Layouts;
using Jellyfin.Plugin.ExperienceManager.Rules;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.ExperienceManager.Tests;

public sealed class RuleTests : IDisposable
{
    private static readonly User Viewer = new("viewer", "auth", "reset") { Id = Guid.NewGuid() };

    private readonly TempDatabase _db = new();
    private readonly RulesStore _store;
    private readonly ILocalizationManager _localization = Substitute.For<ILocalizationManager>();
    private readonly IUserDataManager _userData = Substitute.For<IUserDataManager>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));

    public RuleTests()
    {
        _store = new RulesStore(_db.Open());
        foreach (var (name, score) in new[] { ("G", 0), ("PG", 10), ("PG-13", 13), ("R", 17) })
        {
            _localization.GetRatingScore(name, null).Returns(new ParentalRatingScore(score, null));
        }

        _userData.GetUserDataBatch(Arg.Any<IReadOnlyList<BaseItem>>(), Viewer).Returns(new Dictionary<Guid, UserItemData>());
    }

    public void Dispose() => _db.Dispose();

    private RuleFilter Filter() => new(_store, _localization, _userData, _time);

    private static Movie Film(string name, string? rating = null, bool poster = true, bool backdrop = true, bool logo = false, string? tmdb = null, params string[] tags)
    {
        var images = new List<ItemImageInfo>();
        if (poster)
        {
            images.Add(new ItemImageInfo { Type = ImageType.Primary, Path = "p" });
        }

        if (backdrop)
        {
            images.Add(new ItemImageInfo { Type = ImageType.Backdrop, Path = "b" });
        }

        if (logo)
        {
            images.Add(new ItemImageInfo { Type = ImageType.Logo, Path = "l" });
        }

        var film = new Movie { Id = Guid.NewGuid(), Name = name, OfficialRating = rating, ImageInfos = images.ToArray(), Tags = tags, RunTimeTicks = 100 };
        if (tmdb is not null)
        {
            film.ProviderIds = new Dictionary<string, string> { ["Tmdb"] = tmdb };
        }

        return film;
    }

    private static RuleTarget Row(string type = SectionTypes.Query, params string[] off) => new(type, false, off);

    private static RuleTarget Banner(params string[] off) => new(SectionTypes.Featured, true, off);

    private static IEnumerable<string> Names(IEnumerable<BaseItem> items) => items.Select(i => i.Name);

    [Fact]
    public void ExcludedTagsLeaveEverySectionUnlessItOptsOut()
    {
        _store.Set(ContentRules.Defaults with { ExcludeTags = true, ExcludedTags = ["Hidden"] });
        BaseItem[] items = [Film("kept"), Film("tagged", tags: "hidden")];

        Assert.Equal(["kept"], Names(Filter().Apply(Row(), Viewer, [], items)));
        Assert.Equal(["kept"], Names(Filter().Apply(Banner(), Viewer, [], items)));
        Assert.Equal(["kept", "tagged"], Names(Filter().Apply(Row(SectionTypes.Query, RuleIds.Tags), Viewer, [], items)));
    }

    [Fact]
    public void TheCeilingLimitsFeaturesForTheFirstAudienceWithOneAndKeepsUnratedItemsOutUnderIt()
    {
        BaseItem[] items = [Film("g", "G"), Film("pg13", "PG-13"), Film("r", "R"), Film("unrated")];
        Assert.Equal(["g", "pg13", "r", "unrated"], Names(Filter().Apply(Banner(), Viewer, [], items)));

        _store.Set(ContentRules.Defaults with { CeilingOn = true, EveryoneCeiling = "R", AudienceCeilings = new Dictionary<string, string> { ["kids"] = "PG", ["teens"] = "PG-13" } });

        Assert.Equal(["g", "pg13", "r"], Names(Filter().Apply(Banner(), Viewer, [], items)));
        Assert.Equal(["g"], Names(Filter().Apply(Banner(), Viewer, ["kids", "teens"], items)));
        Assert.Equal(["g", "pg13"], Names(Filter().Apply(Banner(), Viewer, ["teens", "kids"], items)));
        Assert.Equal(["g", "pg13", "r", "unrated"], Names(Filter().Apply(Row(), Viewer, ["kids"], items)));
        Assert.Equal(["g", "pg13", "r", "unrated"], Names(Filter().Apply(Banner(RuleIds.Ceiling), Viewer, ["kids"], items)));
    }

    [Fact]
    public void FeaturesNeedABackdropAndALogoWhenAskedAndRowsNeedPostersOnTitles()
    {
        var noPoster = Film("no poster", poster: false);
        var episode = new Episode { Id = Guid.NewGuid(), Name = "episode" };
        BaseItem[] items = [Film("full", logo: true), Film("no logo"), Film("no backdrop", backdrop: false), noPoster, episode];

        Assert.Equal(["full", "no logo", "no poster"], Names(Filter().Apply(Banner(), Viewer, [], items)));
        Assert.Equal(["full", "no logo", "no backdrop", "episode"], Names(Filter().Apply(Row(), Viewer, [], items)));
        Assert.Contains(noPoster, Filter().Apply(Row(SectionTypes.Query, RuleIds.Poster), Viewer, [], items));

        _store.Set(ContentRules.Defaults with { RequireLogo = true });
        Assert.Equal(["full"], Names(Filter().Apply(Banner(), Viewer, [], items)));
        Assert.Equal(5, Filter().Apply(Banner(RuleIds.FeatureArt), Viewer, [], items).Count);
    }

    [Fact]
    public void FilmsSharingAProviderIdShowOnce()
    {
        BaseItem[] items = [Film("4k", tmdb: "603"), Film("hd", tmdb: "603"), Film("other", tmdb: "604"), Film("unknown"), Film("unknown too")];

        Assert.Equal(["4k", "other", "unknown", "unknown too"], Names(Filter().Apply(Row(), Viewer, [], items)));
        Assert.Equal(5, Filter().Apply(Row(SectionTypes.Query, RuleIds.Versions), Viewer, [], items).Count);
    }

    [Fact]
    public void ContinueWatchingCleanUpDropsWhatIsOldOrNearlyFinishedFromOneBatch()
    {
        var old = Film("old");
        var done = Film("done");
        var fresh = Film("fresh");
        _userData.GetUserDataBatch(Arg.Any<IReadOnlyList<BaseItem>>(), Viewer).Returns(new Dictionary<Guid, UserItemData>
        {
            [old.Id] = new() { Key = "o", LastPlayedDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), PlaybackPositionTicks = 10 },
            [done.Id] = new() { Key = "d", LastPlayedDate = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc), PlaybackPositionTicks = 95 },
            [fresh.Id] = new() { Key = "f", LastPlayedDate = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc), PlaybackPositionTicks = 40 },
        });
        BaseItem[] items = [old, done, fresh];
        Assert.Equal(3, Filter().Apply(Row(SectionTypes.ContinueWatching), Viewer, [], items).Count);

        _store.Set(ContentRules.Defaults with { ContinueCleanup = true, CleanupDays = 30, CleanupPercent = 90 });

        Assert.Equal(["fresh"], Names(Filter().Apply(Row(SectionTypes.ContinueWatching), Viewer, [], items)));
        Assert.Equal(3, Filter().Apply(Row(SectionTypes.Query), Viewer, [], items).Count);
        _userData.Received(1).GetUserDataBatch(Arg.Any<IReadOnlyList<BaseItem>>(), Viewer);
    }

    [Fact]
    public void AFreshInstallHasThePosterVersionsAndBackdropRulesOnAndTheRestOff()
    {
        var rules = new RulesStore(_db.Open()).Rules;

        Assert.Equal((true, true, true), (rules.Poster, rules.Versions, rules.FeatureArt));
        Assert.Equal((false, false, false, false), (rules.ExcludeTags, rules.CeilingOn, rules.RequireLogo, rules.ContinueCleanup));
        Assert.Equal(1, _store.Revision);

        _store.Set(rules with { ExcludeTags = true, ExcludedTags = ["x"] });
        Assert.Equal(2, new RulesStore(_db.Open()).Revision);
        Assert.Equal(["x"], new RulesStore(_db.Open()).Rules.ExcludedTags);
    }

    [Fact]
    public void TheAdminRoutesRefuseUnknownRatingsAudiencesAndRulesAndCountOptOuts()
    {
        var pages = _db.Store();
        var audiences = new AudienceStore(_db.Open());
        var optedOut = new StoredSection("nextup", SectionTypes.NextUp, null, new JsonObject { ["RulesOff"] = new JsonArray("poster", "tags") });
        pages.Replace(PageIds.Home, [.. PageStore.HomeSeed.Where(s => s.Id != "nextup"), optedOut], out var error);
        Assert.Null(error);
        Assert.Null(pages.Replace(PageIds.Home, [new StoredSection("x", SectionTypes.NextUp, null, new JsonObject { ["RulesOff"] = new JsonArray("colour") })], out var unknown));
        Assert.Contains("unknown rule", unknown, StringComparison.Ordinal);
        _localization.GetParentalRatings().Returns([new ParentalRating("PG", new ParentalRatingScore(10, null)), new ParentalRating("G", new ParentalRatingScore(0, null))]);
        var controller = new AdminRulesController(_store, pages, audiences, _localization);

        var listed = controller.GetRules().Value!;
        Assert.Equal((1, 1, 0), (listed.OptOuts[RuleIds.Poster], listed.OptOuts[RuleIds.Tags], listed.OptOuts[RuleIds.Versions]));
        Assert.Equal(["G", "PG"], listed.Ratings);

        Assert.IsType<BadRequestObjectResult>(controller.SetRules(ContentRules.Defaults with { CeilingOn = true, EveryoneCeiling = "NC-99" }).Result);
        Assert.IsType<BadRequestObjectResult>(controller.SetRules(ContentRules.Defaults with { AudienceCeilings = new Dictionary<string, string> { ["teens"] = "PG" } }).Result);
        Assert.IsType<BadRequestObjectResult>(controller.SetRules(ContentRules.Defaults with { ContinueCleanup = true }).Result);
        Assert.Equal(2, controller.SetRules(ContentRules.Defaults with { CeilingOn = true, EveryoneCeiling = "PG" }).Value!.Revision);
    }
}

public sealed class RuleOptOutTests : IDisposable
{
    private readonly TempDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void EachLibrarysLatestKeepsTheOptOutsOfLatestForEachLibrary()
    {
        var viewer = new User("viewer", "auth", "reset") { Id = Guid.NewGuid() };
        var films = Guid.NewGuid();
        var pages = _db.Store();
        pages.Replace(PageIds.Home, [new StoredSection("latest", SectionTypes.LatestLibraries, null, new JsonObject { ["RulesOff"] = new JsonArray("tags") })], out _);
        var sources = Substitute.For<Home.IHomeSources>();
        sources.GetViews(viewer).Returns([new Home.LibraryView(films, "Movies", Jellyfin.Data.Enums.CollectionType.movies)]);
        sources.IsVisible(Arg.Any<BaseItem>(), viewer).Returns(true);
        var library = Substitute.For<ILibraryManager>();
        var tagged = new Movie { Id = Guid.NewGuid(), Name = "tagged", Tags = ["hidden"], ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = "p" }] };
        library.GetItemList(Arg.Any<MediaBrowser.Controller.Entities.InternalItemsQuery>()).Returns([tagged]);
        var time = new FakeTimeProvider();
        var rules = new RulesStore(_db.Open());
        rules.Set(ContentRules.Defaults with { ExcludeTags = true, ExcludedTags = ["hidden"] });
        var userData = Substitute.For<IUserDataManager>();
        using var cache = new Caching.HomeCache(library, Substitute.For<MediaBrowser.Controller.TV.ITVSeriesManager>(), userData, time);
        using var timeline = _db.Timeline(time);
        var stored = new Home.StoredPages(pages, timeline, cache, sources, time, null, new RuleFilter(rules, Substitute.For<ILocalizationManager>(), userData, time));

        var latest = stored.SectionsFor(PageIds.Home, viewer, 0).Single();

        Assert.Equal("latest-" + films.ToString("N"), latest.Id);
        Assert.Equal([tagged], latest.Load(viewer).Items);
    }
}
