using System.Collections.Generic;
using Jellyfin.Plugin.ExperienceManager.Layouts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ExperienceManager.Controllers;

/// <summary>
/// A stored page as the admin edits it: its server layout's section instances, not their items.
/// </summary>
/// <param name="PageId">The page id.</param>
/// <param name="LayoutVersion">Changes whenever the layout changes.</param>
/// <param name="Sections">The sections, in order.</param>
/// <param name="SectionTypes">The types a section can have.</param>
public sealed record AdminPageResponse(string PageId, string LayoutVersion, IReadOnlyList<StoredSection> Sections, IReadOnlyList<string> SectionTypes);

/// <summary>
/// A page's new sections.
/// </summary>
/// <param name="Sections">The sections, in order.</param>
public sealed record AdminPageRequest(IReadOnlyList<StoredSection>? Sections);

/// <summary>
/// Reads and replaces stored pages. Administrators only; not part of the published home API.
/// </summary>
[ApiController]
[Route(AdminController.Route + "/Pages")]
[Authorize(Policy = "RequiresElevation")]
public class AdminPagesController : ControllerBase
{
    private readonly PageStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminPagesController"/> class.
    /// </summary>
    /// <param name="store">The stored pages.</param>
    public AdminPagesController(PageStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Gets a stored page's sections.
    /// </summary>
    /// <param name="pageId">The page id; <c>home</c> is the only page.</param>
    /// <returns>The page.</returns>
    [HttpGet("{pageId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<AdminPageResponse> GetPage([FromRoute] string pageId) =>
        _store.Get(pageId) is { } page ? Describe(page) : NotFound();

    /// <summary>
    /// Replaces a stored page's sections. The page call shows the change at once, with a new layout version.
    /// </summary>
    /// <param name="pageId">The page id; <c>home</c> is the only page.</param>
    /// <param name="request">The sections, in order.</param>
    /// <returns>The page as now stored.</returns>
    [HttpPut("{pageId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<AdminPageResponse> PutPage([FromRoute] string pageId, [FromBody] AdminPageRequest request)
    {
        if (_store.Get(pageId) is null)
        {
            return NotFound();
        }

        var page = _store.Replace(pageId, request.Sections ?? [], out var error);
        return page is null ? BadRequest(new { Error = error }) : Describe(page);
    }

    private static AdminPageResponse Describe(StoredPage page) => new(page.Id, page.LayoutVersion, page.Sections, SectionTypes.All);
}
