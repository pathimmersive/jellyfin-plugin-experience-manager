using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Library;

namespace Jellyfin.Plugin.ExperienceManager.Home;

/// <summary>
/// A library the user can see, as the home page lists it.
/// </summary>
/// <param name="Id">The library's id.</param>
/// <param name="Name">The library's name.</param>
/// <param name="Type">What it holds.</param>
/// <param name="Item">The library's own folder, for drawing it as a tile.</param>
public sealed record LibraryView(Guid Id, string Name, CollectionType? Type, BaseItem? Item = null);

/// <summary>
/// What the home page needs to know about a user that only the server can answer.
/// </summary>
public interface IHomeSources
{
    /// <summary>
    /// Gets the libraries the user can see, in the user's own order.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>The libraries.</returns>
    IReadOnlyList<LibraryView> GetViews(User user);

    /// <summary>
    /// Gets a value indicating whether Jellyfin would show the item to the user, by library access
    /// and parental controls.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="user">The user.</param>
    /// <returns>Whether the user may see it.</returns>
    bool IsVisible(BaseItem item, User user);
}

/// <summary>
/// Answers from Jellyfin itself.
/// </summary>
public sealed class JellyfinHomeSources : IHomeSources
{
    private readonly IUserViewManager _views;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinHomeSources"/> class.
    /// </summary>
    /// <param name="views">Jellyfin's user views.</param>
    public JellyfinHomeSources(IUserViewManager views)
    {
        _views = views;
    }

    /// <inheritdoc />
    public IReadOnlyList<LibraryView> GetViews(User user) =>
        _views.GetUserViews(new UserViewQuery { User = user })
            .Select(v => new LibraryView(v.Id, v.Name, (v as ICollectionFolder)?.CollectionType, v))
            .ToList();

    /// <inheritdoc />
    public bool IsVisible(BaseItem item, User user) => item.IsVisibleStandalone(user);
}
