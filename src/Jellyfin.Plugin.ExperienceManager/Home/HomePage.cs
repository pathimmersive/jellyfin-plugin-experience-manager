using System;
using System.Collections.Generic;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.ExperienceManager.Home;

/// <summary>
/// What a section shows, said without loading it, so a reach adapter can map it to a client's own row
/// types: a built-in kind, a library's latest items, a library query, a collection or playlist, or one of
/// Experience Manager's own sections.
/// </summary>
/// <param name="Kind">One of the <see cref="SectionSources"/> kinds.</param>
/// <param name="ParentId">The library, collection or playlist it reads, if any.</param>
/// <param name="ItemTypes">The item types it lists, as Jellyfin's <c>includeItemTypes</c>, if any.</param>
public sealed record SectionSource(string Kind, string? ParentId = null, string? ItemTypes = null);

/// <summary>
/// The kinds of <see cref="SectionSource"/>.
/// </summary>
public static class SectionSources
{
    /// <summary>The featured banner.</summary>
    public const string Featured = "featured";

    /// <summary>The user's libraries.</summary>
    public const string MyMedia = "mymedia";

    /// <summary>Items the user started and has not finished.</summary>
    public const string ContinueWatching = "continue";

    /// <summary>The next episode of each show the user is watching.</summary>
    public const string NextUp = "nextup";

    /// <summary>The latest items in one library.</summary>
    public const string Latest = "latest";

    /// <summary>A Jellyfin item query over one library.</summary>
    public const string Query = "query";

    /// <summary>A collection or playlist.</summary>
    public const string Collection = "collection";

    /// <summary>An Experience Manager section no client knows natively.</summary>
    public const string Section = "section";
}

/// <summary>
/// One section of a page and how to load its items for a user.
/// </summary>
/// <param name="Id">The section's stable id.</param>
/// <param name="Title">The title shown above it.</param>
/// <param name="Display">How to show it.</param>
/// <param name="Source">What it shows, without loading it.</param>
/// <param name="SeeAll">Where "see all" leads, or null.</param>
/// <param name="FirstPage">How many items the page call inlines.</param>
/// <param name="Load">Loads every item the user may see, in order, and whether they came from the cache.</param>
public sealed record PageSection(
    string Id,
    string Title,
    SectionDisplay Display,
    SectionSource Source,
    SeeAllTarget? SeeAll,
    int FirstPage,
    Func<User, (IReadOnlyList<BaseItem> Items, bool Hit)> Load);

/// <summary>
/// The pages, each resolved into its sections for a user.
/// </summary>
public interface IPages
{
    /// <summary>
    /// Gets a page's layout version, which changes whenever its layout or the events on it do.
    /// </summary>
    /// <param name="pageId">The page id.</param>
    /// <param name="utcOffset">The viewer's UTC offset in minutes, or null for the server's time zone.</param>
    /// <param name="user">The viewer, whose audiences decide their layout and events; null for the server layout.</param>
    /// <returns>The version, or null if there is no such page.</returns>
    string? LayoutVersion(string pageId, int? utcOffset = null, User? user = null);

    /// <summary>
    /// Gets a page's sections for a user, in order, with the events on in the viewer's local time.
    /// </summary>
    /// <param name="pageId">The page id.</param>
    /// <param name="user">The user.</param>
    /// <param name="utcOffset">The viewer's UTC offset in minutes, or null for the server's time zone.</param>
    /// <returns>The sections, or none if there is no such page.</returns>
    IReadOnlyList<PageSection> SectionsFor(string pageId, User user, int? utcOffset = null);
}
