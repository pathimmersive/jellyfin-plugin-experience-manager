using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Jellyfin.Plugin.ExperienceManager.Layouts;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExperienceManager.Home;

/// <summary>
/// What a client asked for in its items: Jellyfin's own <c>fields</c>, <c>enableImageTypes</c>,
/// <c>imageTypeLimit</c> and <c>enableUserData</c>. Anything left out takes the section's small default.
/// </summary>
/// <param name="Fields">Extra fields.</param>
/// <param name="ImageTypes">The image types to include.</param>
/// <param name="ImageTypeLimit">How many images of each type.</param>
/// <param name="EnableUserData">Whether to include the user's played state and progress.</param>
public sealed record ItemShape(IReadOnlyList<ItemFields>? Fields, IReadOnlyList<ImageType>? ImageTypes, int? ImageTypeLimit, bool? EnableUserData)
{
    /// <summary>Gets the shape with every default.</summary>
    public static ItemShape Default { get; } = new(null, null, null, null);
}

/// <summary>
/// How long one section took and whether its data came from the cache, for Server-Timing.
/// </summary>
/// <param name="SectionId">The section.</param>
/// <param name="Hit">Whether its data came from the cache.</param>
/// <param name="Milliseconds">How long it took.</param>
public sealed record SectionTiming(string SectionId, bool Hit, double Milliseconds);

/// <summary>
/// Builds pages and section items for a user from the page's sections.
/// </summary>
public sealed partial class HomePageService
{
    private readonly IPages _pages;
    private readonly IDtoService _dto;
    private readonly ILogger<HomePageService> _logger;
    private readonly Health.SectionTimings? _timings;

    /// <summary>
    /// Initializes a new instance of the <see cref="HomePageService"/> class.
    /// </summary>
    /// <param name="pages">The pages.</param>
    /// <param name="dto">Jellyfin's DTO service.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timings">Where each section's load time is recorded, for Slow.</param>
    public HomePageService(IPages pages, IDtoService dto, ILogger<HomePageService> logger, Health.SectionTimings timings)
        : this(pages, dto, logger)
    {
        _timings = timings;
    }

    internal HomePageService(IPages pages, IDtoService dto, ILogger<HomePageService> logger)
    {
        _pages = pages;
        _dto = dto;
        _logger = logger;
    }

    /// <summary>
    /// Builds the page for a user. A section whose items fail carries an error; the others are unaffected.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="pageId">The page id.</param>
    /// <param name="itemSections">How many sections, from the top, carry their first items inline.</param>
    /// <param name="shape">The item shape the client asked for.</param>
    /// <param name="utcOffset">The viewer's UTC offset in minutes, or null for the server's time zone.</param>
    /// <param name="timings">Each section's timing.</param>
    /// <returns>The page, or null if there is no such page.</returns>
    public PageResponse? BuildPage(User user, string pageId, int itemSections, ItemShape shape, int? utcOffset, out IReadOnlyList<SectionTiming> timings)
    {
        var layoutVersion = _pages.LayoutVersion(pageId, utcOffset, user);
        if (layoutVersion is null)
        {
            timings = [];
            return null;
        }

        var sections = _pages.SectionsFor(pageId, user, utcOffset);
        var built = new SectionResponse[sections.Count];
        var measured = new SectionTiming?[sections.Count];

        // The sections' queries are independent, so they run side by side: a cold page costs its
        // slowest section, not the sum of them.
        Parallel.For(0, sections.Count, i =>
        {
            var section = sections[i];
            QueryResult<BaseItemDto>? items = null;
            string? error = null;
            if (i < itemSections && section.Announcement is null)
            {
                var watch = Stopwatch.StartNew();
                var hit = false;
                try
                {
                    var (all, cacheHit) = section.Load(user);
                    hit = cacheHit;
                    items = Page(section, all, 0, section.FirstPage, shape, user);
                }
                catch (Exception exception)
                {
                    LogSectionFailed(_logger, section.Id, exception);
                    error = "unavailable";
                }

                measured[i] = new SectionTiming(section.Id, hit, watch.Elapsed.TotalMilliseconds);
                _timings?.Record(section.Id, watch.Elapsed.TotalMilliseconds);
            }

            built[i] = section.Announcement is { } announcement
                ? new SectionResponse(section.Id, section.Title, "announcement", section.Display, false, null, null, null, announcement)
                : new SectionResponse(section.Id, section.Title, "items", section.Display, false, section.SeeAll, items, error);
        });

        timings = measured.OfType<SectionTiming>().ToList();
        return new PageResponse(pageId.ToLowerInvariant(), layoutVersion, built);
    }

    /// <summary>
    /// Finds a section on a user's Home as it is served to them now.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="sectionId">The section id.</param>
    /// <param name="utcOffset">The viewer's UTC offset in minutes, for the events on.</param>
    /// <returns>The section, or null when the user's Home has no such section.</returns>
    public PageSection? FindSection(User user, string sectionId, int? utcOffset) =>
        _pages.SectionsFor(PageIds.Home, user, utcOffset).FirstOrDefault(s => string.Equals(s.Id, sectionId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Gets a page of one section's items for a user.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="sectionId">The section.</param>
    /// <param name="startIndex">The first item.</param>
    /// <param name="limit">How many items.</param>
    /// <param name="shape">The item shape the client asked for.</param>
    /// <param name="utcOffset">The viewer's UTC offset in minutes, or null for the server's time zone.</param>
    /// <param name="timing">The section's timing.</param>
    /// <returns>The items, or null when the user's Home has no such section.</returns>
    public QueryResult<BaseItemDto>? GetItems(User user, string sectionId, int startIndex, int limit, ItemShape shape, int? utcOffset, out SectionTiming? timing)
    {
        var section = _pages.SectionsFor(PageIds.Home, user, utcOffset).FirstOrDefault(s => string.Equals(s.Id, sectionId, StringComparison.OrdinalIgnoreCase));
        if (section is null)
        {
            timing = null;
            return null;
        }

        var watch = Stopwatch.StartNew();
        var (all, hit) = section.Load(user);
        var items = Page(section, all, startIndex, limit, shape, user);
        timing = new SectionTiming(section.Id, hit, watch.Elapsed.TotalMilliseconds);
        _timings?.Record(section.Id, watch.Elapsed.TotalMilliseconds);
        return items;
    }

    /// <summary>
    /// Gets the DTO options for a section: its small default, changed by what the client asked for.
    /// </summary>
    /// <param name="feature">Whether the section is a feature, which needs the overview and logo.</param>
    /// <param name="shape">The item shape the client asked for.</param>
    /// <returns>The options.</returns>
    internal static DtoOptions OptionsFor(bool feature, ItemShape shape)
    {
        IReadOnlyList<ItemFields> fields = feature ? [ItemFields.Overview, ItemFields.Genres] : [];
        IReadOnlyList<ImageType> images = feature
            ? [ImageType.Backdrop, ImageType.Logo, ImageType.Primary]
            : [ImageType.Primary, ImageType.Thumb, ImageType.Backdrop];
        return new DtoOptions(false)
        {
            Fields = (shape.Fields ?? fields).ToArray(),
            ImageTypes = (shape.ImageTypes ?? images).ToArray(),
            ImageTypeLimit = shape.ImageTypeLimit ?? 1,
            EnableImages = true,
            EnableUserData = shape.EnableUserData ?? true,
        };
    }

    // One batched DTO call for the page of items; never one call per item.
    private QueryResult<BaseItemDto> Page(PageSection section, IReadOnlyList<BaseItem> all, int startIndex, int limit, ItemShape shape, User user)
    {
        var start = Math.Max(0, startIndex);
        var take = all.Skip(start).Take(Math.Max(0, limit)).ToList();
        var dtos = take.Count == 0 ? [] : _dto.GetBaseItemDtos(take, OptionsFor(section.Display.Hint == "feature", shape), user);
        return new QueryResult<BaseItemDto>(start, all.Count, dtos);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Home section {SectionId} failed; the rest of the page is served")]
    private static partial void LogSectionFailed(ILogger logger, string sectionId, Exception exception);
}
