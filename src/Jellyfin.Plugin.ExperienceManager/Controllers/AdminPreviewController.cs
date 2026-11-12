using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ExperienceManager.Home;
using Jellyfin.Plugin.ExperienceManager.Programming;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ExperienceManager.Controllers;

/// <summary>
/// What to preview: a moment, and optionally an event being edited.
/// </summary>
/// <param name="At">The local date and time, <c>yyyy-MM-ddTHH:mm</c>, in the server's time zone.</param>
/// <param name="Event">An event being edited, applied even as a draft or before it is saved; it replaces the stored event with its id.</param>
/// <param name="Audience">Preview as a member of this audience; left out, as the edited event's audience, else as the viewer.</param>
/// <param name="UserId">Preview as this user, with their libraries, parental controls and audiences; the signed-in admin when left out.</param>
/// <param name="Items">How many items to give each section, up to 24; none when left out.</param>
public sealed record AdminPreviewRequest(string? At, ProgrammedEvent? Event, string? Audience = null, Guid? UserId = null, int? Items = null);

/// <summary>
/// A page as it would be at a moment.
/// </summary>
/// <param name="At">The moment previewed.</param>
/// <param name="Events">The events on at that moment, highest first.</param>
/// <param name="Sections">The sections, in order, each with the event that changed it.</param>
/// <param name="Hidden">The sections events hid.</param>
/// <param name="Viewer">Whose Home it is.</param>
/// <param name="Audiences">The audiences that count for the viewer, highest first, by name.</param>
/// <param name="Layout">Whose layout the viewer gets: an audience's name, or Everyone's.</param>
/// <param name="LayoutAudience">That audience's id, or null for Everyone's.</param>
public sealed record AdminPreviewResponse(string At, IReadOnlyList<string> Events, IReadOnlyList<PreviewSection> Sections, IReadOnlyList<PreviewHidden> Hidden, string Viewer, IReadOnlyList<string> Audiences, string Layout, string? LayoutAudience);

/// <summary>
/// A layout's badges.
/// </summary>
/// <param name="Badges">One per section and thing to say about it.</param>
public sealed record AdminBadgesResponse(IReadOnlyList<SectionBadge> Badges);

/// <summary>
/// Previews a page at a moment, as the signed-in admin would see it, and says what the layout editor shows on each
/// section. Administrators only; nothing is stored.
/// </summary>
[ApiController]
[Route(AdminController.Route + "/Pages")]
[Authorize(Policy = "RequiresElevation")]
public class AdminPreviewController : ControllerBase
{
    private readonly StoredPages _pages;
    private readonly EventStore _events;
    private readonly IUserManager _users;
    private readonly Audiences.AudienceStore? _audiences;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminPreviewController"/> class.
    /// </summary>
    /// <param name="pages">The stored pages.</param>
    /// <param name="events">The events.</param>
    /// <param name="users">The user manager.</param>
    /// <param name="audiences">The audiences, for their names.</param>
    public AdminPreviewController(StoredPages pages, EventStore events, IUserManager users, Audiences.AudienceStore audiences)
        : this(pages, events, users)
    {
        _audiences = audiences;
    }

    internal AdminPreviewController(StoredPages pages, EventStore events, IUserManager users)
    {
        _pages = pages;
        _events = events;
        _users = users;
    }

    /// <summary>
    /// Gets the badges for a layout's sections now: the event adding, changing or hiding each, and Source missing.
    /// </summary>
    /// <param name="pageId">The page id; <c>home</c> is the only page.</param>
    /// <param name="audience">The audience whose layout it is; Everyone's when left out.</param>
    /// <returns>The badges.</returns>
    [HttpGet("{pageId}/Badges")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<AdminBadgesResponse> GetBadges([FromRoute] string pageId, [FromQuery] string? audience) =>
        _pages.LayoutVersion(pageId) is null ? NotFound() : new AdminBadgesResponse(_pages.Badges(pageId, audience, DraftEventsNow()));

    // The draft's published events on now in the server's time zone: the badges show the draft.
    private List<ProgrammedEvent> DraftEventsNow()
    {
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.Local).DateTime;
        return _events.All.Where(e => e.Published && Schedules.IsActive(e.Schedule, now)).ToList();
    }

    /// <summary>
    /// Previews a page at a local date and time with the published events on then, and the event being edited.
    /// </summary>
    /// <param name="pageId">The page id; <c>home</c> is the only page.</param>
    /// <param name="request">The moment, and the event being edited.</param>
    /// <returns>The page's sections, each marked with the event that changed it.</returns>
    [HttpPost("{pageId}/Preview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<AdminPreviewResponse> Preview([FromRoute] string pageId, [FromBody] AdminPreviewRequest request)
    {
        var user = Guid.TryParse(User.FindFirst(HomeApiController.UserIdClaim)?.Value, out var id) ? _users.GetUserById(id) : null;
        if (user is null)
        {
            return Forbid();
        }

        if (request.UserId is { } viewerId)
        {
            user = _users.GetUserById(viewerId);
            if (user is null)
            {
                return BadRequest(new { Error = $"there is no user {viewerId:N}" });
            }
        }

        if (_pages.LayoutVersion(pageId) is null)
        {
            return NotFound();
        }

        if (Schedules.LocalTime(request.At) is not { } at)
        {
            return BadRequest(new { Error = "At is a local date and time, yyyy-MM-ddTHH:mm" });
        }

        var events = _events.All.ToList();
        var edited = request.Event;
        if (edited is not null)
        {
            if (_events.Check(edited) is { } error)
            {
                return BadRequest(new { Error = error });
            }

            var index = events.FindIndex(e => string.Equals(e.Id, edited.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                events[index] = edited;
            }
            else
            {
                events.Add(edited);
            }
        }

        var active = events.Where(e => (e.Published || ReferenceEquals(e, edited)) && Schedules.IsActive(e.Schedule, at)).ToList();
        var audience = request.Audience ?? (edited is { Audience: not EventAudiences.Everyone } ? edited.Audience : null);
        var preview = _pages.Preview(pageId, user, active, audience, Math.Clamp(request.Items ?? 0, 0, 24));
        string Name(string audienceId) => _audiences?.Get(audienceId)?.Name ?? audienceId;
        return new AdminPreviewResponse(
            request.At!,
            active.Where(e => e.Audience == EventAudiences.Everyone || preview.Audiences.Contains(e.Audience, StringComparer.OrdinalIgnoreCase)).Select(e => e.Name).ToList(),
            preview.Sections,
            preview.Hidden,
            user.Username,
            preview.Audiences.Select(Name).ToList(),
            preview.LayoutAudience is null ? "Everyone's" : Name(preview.LayoutAudience) + "'s",
            preview.LayoutAudience);
    }
}
