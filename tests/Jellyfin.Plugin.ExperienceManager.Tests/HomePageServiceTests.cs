using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ExperienceManager.Home;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.ExperienceManager.Tests;

public class HomePageServiceTests
{
    private static readonly User Viewer = new("viewer", "auth", "reset");

    private readonly IDtoService _dto = Substitute.For<IDtoService>();

    public HomePageServiceTests()
    {
        _dto.GetBaseItemDtos(Arg.Any<IReadOnlyList<BaseItem>>(), Arg.Any<DtoOptions>(), Arg.Any<User?>(), Arg.Any<BaseItem?>())
            .Returns(c => c.Arg<IReadOnlyList<BaseItem>>().Select(i => new BaseItemDto { Id = i.Id, Name = i.Name }).ToList());
    }

    internal static Movie Film(string name) => new() { Id = Guid.NewGuid(), Name = name };

    private static PageSection Section(string id, int count, string hint = "row", Func<User, (IReadOnlyList<BaseItem>, bool)>? load = null) =>
        new(id, id, new SectionDisplay(hint, "landscape"), new SectionSource(SectionSources.Section), null, 3, load ?? (_ => (Enumerable.Range(0, count).Select(i => (BaseItem)Film($"{id}-{i}")).ToList(), true)));

    private HomePageService Service(params PageSection[] sections)
    {
        var page = Substitute.For<IHomePage>();
        page.LayoutVersion.Returns("test-1");
        page.SectionsFor(Arg.Any<User>()).Returns(sections);
        return new HomePageService(page, _dto, NullLogger<HomePageService>.Instance);
    }

    [Fact]
    public void ThePageListsTheSectionsInOrderAndInlinesItemsOnlyForTheFirstOnesAskedFor()
    {
        var service = Service(Section("featured", 5, "feature"), Section("continue", 2), Section("latest", 10));

        var page = service.BuildPage(Viewer, 2, ItemShape.Default, out var timings);

        Assert.Equal("test-1", page.LayoutVersion);
        Assert.Equal(["featured", "continue", "latest"], page.Sections.Select(s => s.Id));
        Assert.Equal(3, page.Sections[0].Items!.Items.Count);
        Assert.Equal(5, page.Sections[0].Items!.TotalRecordCount);
        Assert.Equal(2, page.Sections[1].Items!.Items.Count);
        Assert.Null(page.Sections[2].Items);
        Assert.All(page.Sections, s => Assert.Equal("items", s.Kind));
        Assert.Equal(["featured", "continue"], timings.Select(t => t.SectionId));
    }

    [Fact]
    public void AFailingSectionCarriesAnErrorAndTheOthersTheirItems()
    {
        var service = Service(Section("featured", 5), Section("continue", 0, load: _ => throw new InvalidOperationException("broken")), Section("latest", 4));

        var page = service.BuildPage(Viewer, int.MaxValue, ItemShape.Default, out _);

        Assert.Equal("unavailable", page.Sections[1].Error);
        Assert.Null(page.Sections[1].Items);
        Assert.Equal(3, page.Sections[0].Items!.Items.Count);
        Assert.Equal(3, page.Sections[2].Items!.Items.Count);
    }

    [Fact]
    public void EachSectionsItemsAreBuiltInOneBatchedCall()
    {
        var service = Service(Section("a", 10), Section("b", 10), Section("c", 10));

        service.BuildPage(Viewer, int.MaxValue, ItemShape.Default, out _);

        _dto.ReceivedWithAnyArgs(3).GetBaseItemDtos(default!, default!, default, default);
    }

    [Fact]
    public void SectionItemsArePagedWithTheTotalCount()
    {
        var service = Service(Section("latest", 10));

        var items = service.GetItems(Viewer, "latest", 4, 3, ItemShape.Default, out var timing);

        Assert.NotNull(items);
        Assert.Equal(4, items.StartIndex);
        Assert.Equal(10, items.TotalRecordCount);
        Assert.Equal(["latest-4", "latest-5", "latest-6"], items.Items.Select(i => i.Name));
        Assert.Equal("latest", timing!.SectionId);
    }

    [Fact]
    public void AnUnknownSectionHasNoItems()
    {
        Assert.Null(Service(Section("latest", 10)).GetItems(Viewer, "nope", 0, 10, ItemShape.Default, out _));
    }

    [Fact]
    public void ItemsGetASmallDefaultShapeThatTheClientCanChange()
    {
        var row = HomePageService.OptionsFor(false, ItemShape.Default);
        Assert.Empty(row.Fields);
        Assert.Equal([ImageType.Primary, ImageType.Thumb, ImageType.Backdrop], row.ImageTypes);
        Assert.Equal(1, row.ImageTypeLimit);
        Assert.True(row.EnableUserData);

        var feature = HomePageService.OptionsFor(true, ItemShape.Default);
        Assert.Contains(ItemFields.Overview, feature.Fields);
        Assert.Contains(ImageType.Logo, feature.ImageTypes);

        var asked = HomePageService.OptionsFor(false, new ItemShape([ItemFields.Genres], [ImageType.Primary], 2, false));
        Assert.Equal([ItemFields.Genres], asked.Fields);
        Assert.Equal([ImageType.Primary], asked.ImageTypes);
        Assert.Equal(2, asked.ImageTypeLimit);
        Assert.False(asked.EnableUserData);
    }
}
