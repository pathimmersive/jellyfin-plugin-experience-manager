using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ExperienceManager.Caching;
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
/// A page's sections for a user.
/// </summary>
public interface IHomePage
{
    /// <summary>
    /// Gets the layout version, which changes whenever the layout does.
    /// </summary>
    string LayoutVersion { get; }

    /// <summary>
    /// Gets the page's sections for a user, in order.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>The sections.</returns>
    IReadOnlyList<PageSection> SectionsFor(User user);
}

/// <summary>
/// The Home page until layouts can be edited: featured, My Media, Continue Watching, Next Up, then
/// Latest for each movie and show library the user can see, in the user's library order.
/// </summary>
public sealed class FixedHomePage : IHomePage
{
    /// <summary>How many featured items the page shows.</summary>
    internal const int FeaturedCount = 5;

    /// <summary>How many cards a row shows before "see all".</summary>
    internal const int RowCount = 16;

    private readonly HomeCache _cache;
    private readonly IHomeSources _sources;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="FixedHomePage"/> class.
    /// </summary>
    /// <param name="cache">The home cache.</param>
    /// <param name="sources">The user's libraries and what they may see.</param>
    public FixedHomePage(HomeCache cache, IHomeSources sources)
        : this(cache, sources, TimeProvider.System)
    {
    }

    internal FixedHomePage(HomeCache cache, IHomeSources sources, TimeProvider time)
    {
        _cache = cache;
        _sources = sources;
        _time = time;
    }

    /// <inheritdoc />
    public string LayoutVersion => "fixed-1";

    /// <inheritdoc />
    public IReadOnlyList<PageSection> SectionsFor(User user)
    {
        var views = _sources.GetViews(user);
        var sections = new List<PageSection>
        {
            new("featured", "Featured", new SectionDisplay("feature", "landscape"), new SectionSource(SectionSources.Featured), null, FeaturedCount, u => Featured(u)),
            new("mymedia", "My Media", new SectionDisplay("row", "landscape"), new SectionSource(SectionSources.MyMedia), null, RowCount, _ => (views.Select(v => v.Item).OfType<BaseItem>().ToList(), true)),
            new(
                "continue",
                "Continue Watching",
                new SectionDisplay("row", "landscape"),
                new SectionSource(SectionSources.ContinueWatching),
                new SeeAllTarget(null, "Movie,Episode", "DatePlayed", "Descending", "IsResumable"),
                RowCount,
                u => Visible(u, _cache.ContinueWatching(u, out var hit), hit)),
            new("nextup", "Next Up", new SectionDisplay("row", "landscape"), new SectionSource(SectionSources.NextUp), null, RowCount, u => Visible(u, _cache.NextUp(u, out var hit), hit)),
        };

        foreach (var view in views.Where(v => v.Type is CollectionType.movies or CollectionType.tvshows))
        {
            var shows = view.Type == CollectionType.tvshows;
            var library = view.Id.ToString("N");
            sections.Add(new PageSection(
                "latest-" + library,
                "Latest " + view.Name,
                new SectionDisplay("row", "portrait"),
                new SectionSource(SectionSources.Latest, library, shows ? "Series" : "Movie"),
                new SeeAllTarget(library, shows ? "Series" : "Movie", shows ? "DateLastContentAdded" : "DateCreated", "Descending", null),
                RowCount,
                u => Visible(u, _cache.Latest(view.Id, view.Type, out var hit), hit)));
        }

        return sections;
    }

    // The featured items change within the day so the banner does not always open on the same film,
    // but stay the same for an hour so a page reload does not reshuffle it.
    private (IReadOnlyList<BaseItem> Items, bool Hit) Featured(User user)
    {
        var (visible, hit) = Visible(user, _cache.Featured(out var cacheHit), cacheHit);
        if (visible.Count <= FeaturedCount)
        {
            return (visible, hit);
        }

        var start = _time.GetUtcNow().Hour % (visible.Count - FeaturedCount + 1);
        return (visible.Skip(start).Concat(visible.Take(start)).ToList(), hit);
    }

    private (IReadOnlyList<BaseItem> Items, bool Hit) Visible(User user, IReadOnlyList<BaseItem> items, bool hit) =>
        (items.Where(i => _sources.IsVisible(i, user)).ToList(), hit);
}
