using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ExperienceManager.Publishing;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ExperienceManager.Controllers;

/// <summary>
/// The draft against what is published.
/// </summary>
/// <param name="HasDraft">Whether anything is unpublished.</param>
/// <param name="Changes">What the draft changes, one line each.</param>
/// <param name="Version">The published version Home serves.</param>
/// <param name="CanUndo">Whether Undo has a change to step back.</param>
public sealed record AdminDraftResponse(bool HasDraft, IReadOnlyList<string> Changes, int Version, bool CanUndo);

/// <summary>
/// History: published versions and the other lines, newest first.
/// </summary>
/// <param name="Version">The published version Home serves.</param>
/// <param name="Versions">The published versions History keeps.</param>
/// <param name="Entries">Lines nobody published, such as an event starting.</param>
public sealed record AdminHistoryResponse(int Version, IReadOnlyList<PublishedVersion> Versions, IReadOnlyList<HistoryEntry> Entries);

/// <summary>
/// Publishes the draft, steps it back or drops it, and lists and restores published versions. Administrators only;
/// not part of the published home API.
/// </summary>
[ApiController]
[Route(AdminController.Route)]
[Authorize(Policy = "RequiresElevation")]
public class AdminPublishController : ControllerBase
{
    private readonly PublishStore _publish;
    private readonly IUserManager _users;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminPublishController"/> class.
    /// </summary>
    /// <param name="publish">Drafts and versions.</param>
    /// <param name="users">The user manager, for the author's name.</param>
    public AdminPublishController(PublishStore publish, IUserManager users)
    {
        _publish = publish;
        _users = users;
    }

    /// <summary>
    /// Gets the draft: whether there is one, and what it changes.
    /// </summary>
    /// <returns>The draft.</returns>
    [HttpGet("Publish")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<AdminDraftResponse> GetDraft() => Draft();

    /// <summary>
    /// Publishes the draft: Home serves it from now on.
    /// </summary>
    /// <returns>The new version.</returns>
    [HttpPost("Publish")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<PublishedVersion> Publish()
    {
        var author = Guid.TryParse(User.FindFirst(HomeApiController.UserIdClaim)?.Value, out var id) ? _users.GetUserById(id)?.Username : null;
        return _publish.Publish(author ?? "An administrator") is { } version ? version : BadRequest(new { Error = "there is nothing to publish" });
    }

    /// <summary>
    /// Steps the draft back one change.
    /// </summary>
    /// <returns>The draft.</returns>
    [HttpPost("Publish/Undo")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<AdminDraftResponse> Undo() => _publish.Undo() ? Draft() : BadRequest(new { Error = "there is nothing to undo" });

    /// <summary>
    /// Drops the draft, so the editors hold what is published again.
    /// </summary>
    /// <returns>The draft, now empty.</returns>
    [HttpPost("Publish/Discard")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<AdminDraftResponse> Discard()
    {
        _publish.Discard();
        return Draft();
    }

    /// <summary>
    /// Gets History: the published versions and the other lines, newest first.
    /// </summary>
    /// <returns>History.</returns>
    [HttpGet("History")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<AdminHistoryResponse> GetHistory() => new AdminHistoryResponse(_publish.Version, _publish.Versions(), _publish.Entries());

    /// <summary>
    /// Makes a published version the draft, to publish again.
    /// </summary>
    /// <param name="versionId">The version.</param>
    /// <returns>The draft.</returns>
    [HttpPost("History/{versionId}/Restore")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<AdminDraftResponse> Restore([FromRoute] int versionId) => _publish.Restore(versionId) ? Draft() : NotFound();

    private AdminDraftResponse Draft() => new(_publish.HasDraft, _publish.Changes(), _publish.Version, _publish.CanUndo);
}
