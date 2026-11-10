using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ExperienceManager.Audiences;
using Jellyfin.Plugin.ExperienceManager.Layouts;
using Jellyfin.Plugin.ExperienceManager.Programming;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ExperienceManager.Controllers;

/// <summary>
/// A user, as an audience lists them.
/// </summary>
/// <param name="Id">The user's id.</param>
/// <param name="Name">The user's name.</param>
public sealed record AudienceMember(Guid Id, string Name);

/// <summary>
/// An audience as the admin sees it.
/// </summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Kind"><c>group</c> or <c>new-users</c>.</param>
/// <param name="Days">For New users, how many days an account counts as new.</param>
/// <param name="Members">Its members now: the picked users, or for New users the accounts that are new.</param>
/// <param name="HasLayout">Whether it has its own Home layout.</param>
public sealed record AdminAudience(string Id, string Name, string Kind, int? Days, IReadOnlyList<AudienceMember> Members, bool HasLayout);

/// <summary>
/// The audiences, in precedence order.
/// </summary>
/// <param name="Audiences">Highest first.</param>
/// <param name="Users">Every user, for picking members.</param>
public sealed record AdminAudiencesResponse(IReadOnlyList<AdminAudience> Audiences, IReadOnlyList<AudienceMember> Users);

/// <summary>
/// A new or changed audience.
/// </summary>
/// <param name="Name">Its name; ignored for New users.</param>
/// <param name="Members">Its users; New users has none.</param>
/// <param name="Days">New users' days; ignored for a group.</param>
public sealed record AdminAudienceRequest(string? Name, IReadOnlyList<Guid>? Members, int? Days);

/// <summary>
/// The audiences' new order.
/// </summary>
/// <param name="Ids">Every audience id once, highest first.</param>
public sealed record AdminAudienceOrderRequest(IReadOnlyList<string>? Ids);

/// <summary>
/// A section of a group's layout, with how the group has it.
/// </summary>
/// <param name="Id">The section's id.</param>
/// <param name="Type">Its type.</param>
/// <param name="Title">Its title, or null for the type's own.</param>
/// <param name="Config">Its settings.</param>
/// <param name="Mark"><c>inherited</c>, <c>changed</c>, <c>added</c> (only in the group) or <c>removed</c>.</param>
/// <param name="Off">Whether it is switched off.</param>
public sealed record AdminGroupSection(string Id, string Type, string? Title, System.Text.Json.Nodes.JsonObject? Config, string Mark, bool Off);

/// <summary>
/// A group's layout of a page.
/// </summary>
/// <param name="AudienceId">The audience.</param>
/// <param name="PageId">The page.</param>
/// <param name="HasLayout">Whether the group has its own layout; without one its members see the server layout.</param>
/// <param name="Revision">Counts its edits.</param>
/// <param name="Sections">Its sections in order, removed ones where the group put them.</param>
public sealed record AdminGroupLayoutResponse(string AudienceId, string PageId, bool HasLayout, int Revision, IReadOnlyList<AdminGroupSection> Sections);

/// <summary>
/// A group's new layout of a page.
/// </summary>
/// <param name="Entries">Its entries in order: each section inherited, changed (its own copy), added (only in the group) or removed.</param>
public sealed record AdminGroupLayoutRequest(IReadOnlyList<GroupEntry>? Entries);

/// <summary>
/// Audiences, their members and order, and their own layouts. Administrators only; not part of the published home API.
/// </summary>
[ApiController]
[Route(AdminController.Route + "/Audiences")]
[Authorize(Policy = "RequiresElevation")]
public class AdminAudiencesController : ControllerBase
{
    private readonly AudienceStore _audiences;
    private readonly AudienceResolver _resolver;
    private readonly PageStore _pages;
    private readonly EventStore _events;
    private readonly IUserManager _users;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminAudiencesController"/> class.
    /// </summary>
    /// <param name="audiences">The audiences.</param>
    /// <param name="resolver">Works out who is in New users.</param>
    /// <param name="pages">The stored pages.</param>
    /// <param name="events">The events, which may be for an audience.</param>
    /// <param name="users">The user manager.</param>
    public AdminAudiencesController(AudienceStore audiences, AudienceResolver resolver, PageStore pages, EventStore events, IUserManager users)
    {
        _audiences = audiences;
        _resolver = resolver;
        _pages = pages;
        _events = events;
        _users = users;
    }

    /// <summary>
    /// Gets the audiences in precedence order, with their members, and every user.
    /// </summary>
    /// <returns>The audiences.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<AdminAudiencesResponse> GetAudiences() => List();

    /// <summary>
    /// Adds a group at the bottom of the order.
    /// </summary>
    /// <param name="request">Its name and members.</param>
    /// <returns>The audiences.</returns>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<AdminAudiencesResponse> AddAudience([FromBody] AdminAudienceRequest request)
    {
        if (UnknownMember(request.Members) is { } unknown)
        {
            return BadRequest(new { Error = unknown });
        }

        return _audiences.Add(request.Name, request.Members, out var error) is null ? BadRequest(new { Error = error }) : List();
    }

    /// <summary>
    /// Renames a group and sets its members, or sets New users' days.
    /// </summary>
    /// <param name="audienceId">The audience.</param>
    /// <param name="request">Its name and members, or New users' days.</param>
    /// <returns>The audiences.</returns>
    [HttpPut("{audienceId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<AdminAudiencesResponse> UpdateAudience([FromRoute] string audienceId, [FromBody] AdminAudienceRequest request)
    {
        if (_audiences.Get(audienceId) is null)
        {
            return NotFound();
        }

        if (UnknownMember(request.Members) is { } unknown)
        {
            return BadRequest(new { Error = unknown });
        }

        return _audiences.Update(audienceId, request.Name, request.Members, request.Days, out var error) is null ? BadRequest(new { Error = error }) : List();
    }

    /// <summary>
    /// Deletes a group and its layouts. New users, and an audience an event is for, cannot be deleted.
    /// </summary>
    /// <param name="audienceId">The audience.</param>
    /// <returns>The audiences.</returns>
    [HttpDelete("{audienceId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<AdminAudiencesResponse> DeleteAudience([FromRoute] string audienceId)
    {
        var audience = _audiences.Get(audienceId);
        if (audience is null)
        {
            return NotFound();
        }

        var using_ = _events.All.FirstOrDefault(e => string.Equals(e.Audience, audience.Id, StringComparison.OrdinalIgnoreCase));
        if (using_ is not null)
        {
            return BadRequest(new { Error = $"{using_.Name} is for {audience.Name}; give it another audience first" });
        }

        return _audiences.Delete(audience.Id, out var error) ? List() : BadRequest(new { Error = error });
    }

    /// <summary>
    /// Puts the audiences in a new precedence order, highest first.
    /// </summary>
    /// <param name="request">Every audience id once.</param>
    /// <returns>The audiences in their new order.</returns>
    [HttpPut("Order")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<AdminAudiencesResponse> OrderAudiences([FromBody] AdminAudienceOrderRequest request) =>
        _audiences.Reorder(request.Ids, out var error) is null ? BadRequest(new { Error = error }) : List();

    /// <summary>
    /// Gets a group's layout of a page, each section marked inherited, changed, added or removed.
    /// </summary>
    /// <param name="audienceId">The audience.</param>
    /// <param name="pageId">The page; <c>home</c> is the only page.</param>
    /// <returns>The layout.</returns>
    [HttpGet("{audienceId}/Pages/{pageId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<AdminGroupLayoutResponse> GetLayout([FromRoute] string audienceId, [FromRoute] string pageId) =>
        _audiences.Get(audienceId) is { } audience && _pages.Get(pageId) is { } page ? Describe(audience, page) : NotFound();

    /// <summary>
    /// Replaces a group's layout of a page. The server layout is never written.
    /// </summary>
    /// <param name="audienceId">The audience.</param>
    /// <param name="pageId">The page; <c>home</c> is the only page.</param>
    /// <param name="request">The group's entries, in its order.</param>
    /// <returns>The layout as stored.</returns>
    [HttpPut("{audienceId}/Pages/{pageId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<AdminGroupLayoutResponse> SetLayout([FromRoute] string audienceId, [FromRoute] string pageId, [FromBody] AdminGroupLayoutRequest request)
    {
        if (_audiences.Get(audienceId) is not { } audience || _pages.Get(pageId) is not { } page)
        {
            return NotFound();
        }

        return _audiences.SetLayout(audience.Id, page.Id, page.Sections, request.Entries, out var error) is null
            ? BadRequest(new { Error = error })
            : Describe(audience, page);
    }

    /// <summary>
    /// Removes a group's layout of a page, so its members see the server layout.
    /// </summary>
    /// <param name="audienceId">The audience.</param>
    /// <param name="pageId">The page.</param>
    /// <returns>The layout, now all inherited.</returns>
    [HttpDelete("{audienceId}/Pages/{pageId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<AdminGroupLayoutResponse> ClearLayout([FromRoute] string audienceId, [FromRoute] string pageId)
    {
        if (_audiences.Get(audienceId) is not { } audience || _pages.Get(pageId) is not { } page)
        {
            return NotFound();
        }

        _audiences.ClearLayout(audience.Id, page.Id);
        return Describe(audience, page);
    }

    private AdminGroupLayoutResponse Describe(Audience audience, StoredPage page)
    {
        var layout = _audiences.Layout(audience.Id, page.Id);
        var marked = GroupLayouts.Merge(page.Sections, layout?.Entries ?? []);
        return new AdminGroupLayoutResponse(
            audience.Id,
            page.Id,
            layout is not null,
            layout?.Revision ?? 0,
            marked.Select(m => new AdminGroupSection(m.Section.Id, m.Section.Type, m.Section.Title, m.Section.Config, m.Mark, m.Section.Off)).ToList());
    }

    private string? UnknownMember(IReadOnlyList<Guid>? members) =>
        members?.FirstOrDefault(id => _users.GetUserById(id) is null) is { } unknown && !unknown.Equals(Guid.Empty)
            ? $"there is no user {unknown:N}"
            : null;

    private AdminAudiencesResponse List()
    {
        var users = _users.GetUsers().OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase).ToList();
        var names = users.ToDictionary(u => u.Id, u => u.Username);
        var audiences = _audiences.All.Select(a =>
        {
            var members = a.Kind == AudienceKinds.NewUsers
                ? users.Where(u => _resolver.AudiencesOf(u).Contains(a.Id)).Select(u => new AudienceMember(u.Id, u.Username)).ToList()
                : a.Members.Where(names.ContainsKey).Select(id => new AudienceMember(id, names[id])).ToList();
            return new AdminAudience(a.Id, a.Name, a.Kind, a.Days, members, _audiences.Layout(a.Id, PageIds.Home) is not null);
        }).ToList();
        return new AdminAudiencesResponse(audiences, users.Select(u => new AudienceMember(u.Id, u.Username)).ToList());
    }
}
