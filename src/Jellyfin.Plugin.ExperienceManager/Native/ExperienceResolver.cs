using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ExperienceManager.Home;
using Jellyfin.Plugin.ExperienceManager.Layouts;

namespace Jellyfin.Plugin.ExperienceManager.Native;

/// <summary>
/// One section of a resolved experience: what it is and how to show it, without its items.
/// </summary>
/// <param name="Id">The section's stable id.</param>
/// <param name="Title">The title shown above it.</param>
/// <param name="Display">How to show it.</param>
/// <param name="Source">What it shows.</param>
/// <param name="SeeAll">Where "see all" leads, or null.</param>
public sealed record ResolvedSection(string Id, string Title, SectionDisplay Display, SectionSource Source, SeeAllTarget? SeeAll);

/// <summary>
/// What one user sees on a page on one client family: the sections, in order.
/// </summary>
/// <param name="PageId">The page, <c>home</c>.</param>
/// <param name="LayoutVersion">Changes whenever the layout changes.</param>
/// <param name="FamilyId">The client family it was resolved for.</param>
/// <param name="Sections">The sections, in order.</param>
public sealed record ResolvedExperience(string PageId, string LayoutVersion, string FamilyId, IReadOnlyList<ResolvedSection> Sections);

/// <summary>
/// Resolves the layout into what a user sees on a client family. It never loads a section's items.
/// </summary>
public sealed class ExperienceResolver
{
    private readonly IPages _pages;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExperienceResolver"/> class.
    /// </summary>
    /// <param name="pages">The pages.</param>
    public ExperienceResolver(IPages pages)
    {
        _pages = pages;
    }

    /// <summary>
    /// Resolves Home for a user on a client family.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="familyId">The client family's id.</param>
    /// <returns>The resolved experience.</returns>
    public ResolvedExperience Resolve(User user, string familyId) => new(
        PageIds.Home,
        _pages.LayoutVersion(PageIds.Home) ?? string.Empty,
        familyId,
        _pages.SectionsFor(PageIds.Home, user).Select(s => new ResolvedSection(s.Id, s.Title, s.Display, s.Source, s.SeeAll)).ToList());
}
