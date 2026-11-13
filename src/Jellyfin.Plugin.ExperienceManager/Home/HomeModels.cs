using System.Collections.Generic;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.ExperienceManager.Home;

/// <summary>
/// How a client shows a section. Clients that do not know a hint fall back: an unknown
/// feature style to a plain feature, an unknown feature to a row.
/// </summary>
/// <param name="Hint">Either <c>row</c> or <c>feature</c>.</param>
/// <param name="Shape">The card shape: <c>portrait</c>, <c>landscape</c> or <c>square</c>.</param>
public sealed record SectionDisplay(string Hint, string Shape);

/// <summary>
/// Where "see all" leads: a standard Jellyfin item query a client can run itself.
/// </summary>
/// <param name="ParentId">The library to browse, if the section belongs to one.</param>
/// <param name="IncludeItemTypes">The item types to list.</param>
/// <param name="SortBy">The sort, as Jellyfin's <c>sortBy</c> parameter.</param>
/// <param name="SortOrder">Ascending or Descending.</param>
/// <param name="Filters">Jellyfin's <c>filters</c> parameter, if any.</param>
public sealed record SeeAllTarget(string? ParentId, string IncludeItemTypes, string SortBy, string SortOrder, string? Filters);

/// <summary>
/// One section of a page, as the client sees it. Clients need no knowledge of section types:
/// an unknown <see cref="Kind"/> is skipped.
/// </summary>
/// <param name="Id">The section's stable id.</param>
/// <param name="Title">The title shown above it.</param>
/// <param name="Kind">The content kind: <c>items</c>, or <c>announcement</c>, which has no items and carries <see cref="Announcement"/>.</param>
/// <param name="Display">How to show it.</param>
/// <param name="Rank">Whether the cards show their position, as in a Top 10.</param>
/// <param name="SeeAll">Where "see all" leads, or null when there is nowhere to go.</param>
/// <param name="Items">The first items, when the page call asked for them.</param>
/// <param name="Error">Why the items are missing, when the section failed.</param>
/// <param name="Announcement">For an announcement, what it says; null for every other kind.</param>
/// <param name="Chips">A short label to show on some of the section's cards, such as "2 of 3 watched", by item id; null when there are none.</param>
public sealed record SectionResponse(
    string Id,
    string Title,
    string Kind,
    SectionDisplay Display,
    bool Rank,
    SeeAllTarget? SeeAll,
    QueryResult<BaseItemDto>? Items,
    string? Error,
    Announcements.Announcement? Announcement = null,
    IReadOnlyDictionary<string, string>? Chips = null);

/// <summary>
/// A resolved page: its sections in order.
/// </summary>
/// <param name="PageId">The page, <c>home</c>.</param>
/// <param name="LayoutVersion">Changes whenever the layout changes.</param>
/// <param name="Sections">The sections, in order.</param>
public sealed record PageResponse(string PageId, string LayoutVersion, IReadOnlyList<SectionResponse> Sections);

/// <summary>
/// What the plugin offers, for clients deciding whether to use it.
/// </summary>
/// <param name="Version">The plugin version.</param>
/// <param name="ApiVersions">The API versions served.</param>
/// <param name="Features">The features enabled on this server.</param>
public sealed record InfoResponse(string Version, IReadOnlyList<string> ApiVersions, IReadOnlyList<string> Features);
