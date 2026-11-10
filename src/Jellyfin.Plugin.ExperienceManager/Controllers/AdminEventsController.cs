using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ExperienceManager.Layouts;
using Jellyfin.Plugin.ExperienceManager.Programming;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ExperienceManager.Controllers;

/// <summary>
/// Home's events, highest first, and where two published ones change the same section while both are on.
/// </summary>
/// <param name="Events">The events, highest first.</param>
/// <param name="Conflicts">One line per pair of published events that change the same section at the same time.</param>
/// <param name="ScheduleKinds">The schedule kinds.</param>
/// <param name="ChangeKinds">The change kinds.</param>
public sealed record AdminEventsResponse(
    IReadOnlyList<ProgrammedEvent> Events,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> ScheduleKinds,
    IReadOnlyList<string> ChangeKinds);

/// <summary>
/// The events' new order.
/// </summary>
/// <param name="Ids">Every event id once, highest first.</param>
public sealed record AdminEventOrderRequest(IReadOnlyList<string>? Ids);

/// <summary>
/// Lists, edits, orders and publishes Home's events. Administrators only; not part of the published home API.
/// </summary>
[ApiController]
[Route(AdminController.Route + "/Events")]
[Authorize(Policy = "RequiresElevation")]
public class AdminEventsController : ControllerBase
{
    private readonly EventStore _events;
    private readonly PageStore _pages;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminEventsController"/> class.
    /// </summary>
    /// <param name="events">The events.</param>
    /// <param name="pages">The stored pages.</param>
    public AdminEventsController(EventStore events, PageStore pages)
        : this(events, pages, TimeProvider.System)
    {
    }

    internal AdminEventsController(EventStore events, PageStore pages, TimeProvider time)
    {
        _events = events;
        _pages = pages;
        _time = time;
    }

    /// <summary>
    /// Gets Home's events, highest first, with the conflicts between published ones.
    /// </summary>
    /// <returns>The events.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<AdminEventsResponse> GetEvents() => List();

    /// <summary>
    /// Adds an event at the bottom of the list. With no id, one is made from its name.
    /// </summary>
    /// <param name="value">The event.</param>
    /// <returns>The event as stored.</returns>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<ProgrammedEvent> AddEvent([FromBody] ProgrammedEvent value) =>
        _events.Add(value, out var error) is { } added ? added : BadRequest(new { Error = error });

    /// <summary>
    /// Replaces an event, keeping its place in the list.
    /// </summary>
    /// <param name="eventId">The event.</param>
    /// <param name="value">Its new content.</param>
    /// <returns>The event as stored.</returns>
    [HttpPut("{eventId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<ProgrammedEvent> UpdateEvent([FromRoute] string eventId, [FromBody] ProgrammedEvent value)
    {
        if (_events.Get(eventId) is null)
        {
            return NotFound();
        }

        return _events.Update(value with { Id = eventId }, out var error) is { } updated ? updated : BadRequest(new { Error = error });
    }

    /// <summary>
    /// Deletes an event.
    /// </summary>
    /// <param name="eventId">The event.</param>
    /// <returns>No content.</returns>
    [HttpDelete("{eventId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeleteEvent([FromRoute] string eventId) => _events.Delete(eventId) ? NoContent() : NotFound();

    /// <summary>
    /// Puts the events in a new order, highest first.
    /// </summary>
    /// <param name="request">Every event id once.</param>
    /// <returns>The events in their new order.</returns>
    [HttpPut("Order")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<AdminEventsResponse> OrderEvents([FromBody] AdminEventOrderRequest request) =>
        _events.Reorder(request.Ids, out var error) is null ? BadRequest(new { Error = error }) : List();

    /// <summary>
    /// Publishes an event: while its schedule is on, it changes Home.
    /// </summary>
    /// <param name="eventId">The event.</param>
    /// <returns>The event.</returns>
    [HttpPost("{eventId}/Publish")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<ProgrammedEvent> PublishEvent([FromRoute] string eventId) =>
        _events.SetPublished(eventId, true) is { } value ? value : NotFound();

    /// <summary>
    /// Unpublishes an event: it no longer changes Home.
    /// </summary>
    /// <param name="eventId">The event.</param>
    /// <returns>The event.</returns>
    [HttpPost("{eventId}/Unpublish")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<ProgrammedEvent> UnpublishEvent([FromRoute] string eventId) =>
        _events.SetPublished(eventId, false) is { } value ? value : NotFound();

    private AdminEventsResponse List()
    {
        var events = _events.All;
        var now = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone).DateTime;
        var conflicts = EventOverlay.Conflicts(events, _pages.Get(PageIds.Home)?.Sections ?? [], now);
        return new AdminEventsResponse(events, conflicts, [Programming.ScheduleKinds.Once, Programming.ScheduleKinds.Yearly, Programming.ScheduleKinds.Weekly], Programming.ChangeKinds.All);
    }
}
