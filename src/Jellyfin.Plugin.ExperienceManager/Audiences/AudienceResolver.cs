using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ExperienceManager.Layouts;

namespace Jellyfin.Plugin.ExperienceManager.Audiences;

/// <summary>
/// A user's layout of a page: the server's, or the first audience's in precedence order that has one.
/// </summary>
/// <param name="AudienceId">The audience whose layout it is, or null for the server layout.</param>
/// <param name="Revision">That group layout's revision, or 0.</param>
/// <param name="Sections">The sections, in order.</param>
public sealed record ResolvedLayout(string? AudienceId, int Revision, IReadOnlyList<StoredSection> Sections);

/// <summary>
/// Works out which audiences a user is in, in the admin's precedence order, and which layout they get.
/// </summary>
public sealed class AudienceResolver
{
    private readonly AudienceStore _audiences;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudienceResolver"/> class.
    /// </summary>
    /// <param name="audiences">The audiences.</param>
    public AudienceResolver(AudienceStore audiences)
        : this(audiences, TimeProvider.System)
    {
    }

    internal AudienceResolver(AudienceStore audiences, TimeProvider time)
    {
        _audiences = audiences;
        _time = time;
    }

    /// <summary>
    /// Gets the audiences a user is in, highest first: the groups they were picked for, and New users while
    /// their account is younger than its days.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>The audience ids.</returns>
    public IReadOnlyList<string> AudiencesOf(User user)
    {
        var created = _audiences.CreatedAt(user.Id);
        var now = _time.GetUtcNow();
        return _audiences.All
            .Where(a => a.Kind == AudienceKinds.NewUsers
                ? created is { } at && now - at < TimeSpan.FromDays(a.Days ?? AudienceStore.DefaultNewUserDays)
                : a.Members.Contains(user.Id))
            .Select(a => a.Id)
            .ToList();
    }

    /// <summary>
    /// Gets a user's layout of a page: the first of their audiences, in precedence order, with a layout of it, else the server's.
    /// </summary>
    /// <param name="page">The page.</param>
    /// <param name="audiences">The user's audiences, highest first.</param>
    /// <param name="layouts">Where the group layouts come from; the draft when left out.</param>
    /// <returns>The layout.</returns>
    public ResolvedLayout LayoutFor(StoredPage page, IReadOnlyList<string> audiences, Func<string, StoredGroupLayout?>? layouts = null)
    {
        foreach (var id in audiences)
        {
            if ((layouts is null ? _audiences.Layout(id, page.Id) : layouts(id)) is { } layout)
            {
                return new ResolvedLayout(layout.AudienceId, layout.Revision, GroupLayouts.Resolve(page.Sections, layout.Entries));
            }
        }

        return new ResolvedLayout(null, 0, page.Sections);
    }
}
