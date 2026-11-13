using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ExperienceManager.Home;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ExperienceManager.Controllers;

/// <summary>
/// The public home API: pages of section descriptors, paged section items as Jellyfin's own
/// <see cref="BaseItemDto"/>, and discovery. Every route answers for the signed-in user only.
/// </summary>
[ApiController]
[Route(HomeClient.Route)]
public class HomeApiController : ControllerBase
{
    /// <summary>The claim Jellyfin puts the signed-in user's id in.</summary>
    internal const string UserIdClaim = "Jellyfin-UserId";

    /// <summary>The most items one section call returns.</summary>
    internal const int MaxLimit = 100;

    private static readonly string[] ApiVersions = ["v1"];
    private static readonly string[] Features = ["pages", "section-items", "announcements"];

    private readonly HomePageService _home;
    private readonly IUserManager _users;
    private readonly Announcements.DismissalStore? _dismissals;
    private readonly Announcements.AnnouncementImages? _images;

    /// <summary>
    /// Initializes a new instance of the <see cref="HomeApiController"/> class.
    /// </summary>
    /// <param name="home">The home page service.</param>
    /// <param name="users">The user manager.</param>
    /// <param name="dismissals">The announcements each user dismissed.</param>
    /// <param name="images">The images uploaded for announcements.</param>
    public HomeApiController(HomePageService home, IUserManager users, Announcements.DismissalStore dismissals, Announcements.AnnouncementImages images)
        : this(home, users)
    {
        _dismissals = dismissals;
        _images = images;
    }

    internal HomeApiController(HomePageService home, IUserManager users)
    {
        _home = home;
        _users = users;
    }

    /// <summary>
    /// Reports the plugin version, the API versions it serves and its enabled features.
    /// </summary>
    /// <returns>The plugin's information.</returns>
    [HttpGet("Info")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<InfoResponse> GetInfo() => new InfoResponse(HomeClient.Version, ApiVersions, Features);

    /// <summary>
    /// Gets a page's sections for the signed-in user, in order. With <c>include=items</c>, the first
    /// <paramref name="count"/> sections (all of them when it is left out) carry their first items.
    /// </summary>
    /// <param name="pageId">The page; <c>home</c>.</param>
    /// <param name="include">Set to <c>items</c> to inline the first sections' items.</param>
    /// <param name="count">How many sections carry items.</param>
    /// <param name="fields">Extra item fields, as Jellyfin's <c>fields</c>.</param>
    /// <param name="enableImageTypes">The image types to include, as Jellyfin's <c>enableImageTypes</c>.</param>
    /// <param name="imageTypeLimit">How many images of each type.</param>
    /// <param name="enableUserData">Whether to include played state and progress.</param>
    /// <param name="utcOffset">The viewer's UTC offset in minutes east of UTC, which decides which scheduled events are on; the server's time zone when left out.</param>
    /// <returns>The page, or 304 when the client already has it.</returns>
    [HttpGet("v1/Pages/{pageId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<PageResponse> GetPage(
        [FromRoute] string pageId,
        [FromQuery] string? include,
        [FromQuery] int? count,
        [FromQuery] string? fields,
        [FromQuery] string? enableImageTypes,
        [FromQuery] int? imageTypeLimit,
        [FromQuery] bool? enableUserData,
        [FromQuery] int? utcOffset = null)
    {
        var user = SignedInUser();
        if (user is null)
        {
            return Forbid();
        }

        var itemSections = string.Equals(include, "items", StringComparison.OrdinalIgnoreCase) ? Math.Max(0, count ?? int.MaxValue) : 0;
        var page = _home.BuildPage(user, pageId, itemSections, Shape(fields, enableImageTypes, imageTypeLimit, enableUserData), utcOffset, out var timings);
        if (page is null)
        {
            return NotFound();
        }

        SetServerTiming(timings);
        return WithETag(page);
    }

    /// <summary>
    /// Gets a page of one section's items for the signed-in user.
    /// </summary>
    /// <param name="sectionId">The section's id, from the page.</param>
    /// <param name="startIndex">The first item.</param>
    /// <param name="limit">How many items, at most 100.</param>
    /// <param name="fields">Extra item fields, as Jellyfin's <c>fields</c>.</param>
    /// <param name="enableImageTypes">The image types to include.</param>
    /// <param name="imageTypeLimit">How many images of each type.</param>
    /// <param name="enableUserData">Whether to include played state and progress.</param>
    /// <param name="utcOffset">The viewer's UTC offset in minutes east of UTC, which decides which scheduled events are on; the server's time zone when left out.</param>
    /// <returns>The items, or 404 when the user's page has no such section.</returns>
    [HttpGet("v1/Sections/{sectionId}/Items")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<QueryResult<BaseItemDto>> GetSectionItems(
        [FromRoute] string sectionId,
        [FromQuery] int? startIndex,
        [FromQuery] int? limit,
        [FromQuery] string? fields,
        [FromQuery] string? enableImageTypes,
        [FromQuery] int? imageTypeLimit,
        [FromQuery] bool? enableUserData,
        [FromQuery] int? utcOffset = null)
    {
        var user = SignedInUser();
        if (user is null)
        {
            return Forbid();
        }

        var items = _home.GetItems(
            user,
            sectionId,
            startIndex ?? 0,
            Math.Clamp(limit ?? StoredPages.RowCount, 0, MaxLimit),
            Shape(fields, enableImageTypes, imageTypeLimit, enableUserData),
            utcOffset,
            out var timing);
        if (items is null)
        {
            return NotFound();
        }

        SetServerTiming(timing is null ? [] : [timing]);
        return WithETag(items);
    }

    /// <summary>
    /// Dismisses an announcement on the signed-in user's Home, on every device, until it changes.
    /// </summary>
    /// <param name="sectionId">The announcement's section id.</param>
    /// <param name="utcOffset">The viewer's UTC offset in minutes, as sent for the page, for announcements an event adds.</param>
    /// <returns>204, or 404 when the user's Home has no such announcement or it cannot be dismissed.</returns>
    [HttpPost("v1/Sections/{sectionId}/Dismiss")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Dismiss([FromRoute] string sectionId, [FromQuery] int? utcOffset = null)
    {
        var user = SignedInUser();
        if (user is null)
        {
            return Forbid();
        }

        var section = _home.FindSection(user, sectionId, utcOffset);
        if (section?.Announcement is not { Dismissible: true } announcement || _dismissals is null)
        {
            return NotFound();
        }

        _dismissals.Dismiss(user.Id, section.Id, announcement.Version);
        return NoContent();
    }

    /// <summary>
    /// Gets an image uploaded for an announcement. Images are named by their content, so an address never changes
    /// what it serves; no token is needed, as for Jellyfin's own item images.
    /// </summary>
    /// <param name="imageId">The image id, as an announcement's <c>Image</c> path ends.</param>
    /// <returns>The image, or 404.</returns>
    [HttpGet("v1/Images/{imageId}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Produces("image/png", "image/jpeg", "image/webp")]
    public ActionResult GetImage([FromRoute] string imageId)
    {
        if (_images?.Open(imageId) is not { } image)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return File(image, Announcements.AnnouncementImages.ContentType(imageId));
    }

    /// <summary>
    /// Reads a comma-separated list of enum names, ignoring names it does not know.
    /// </summary>
    /// <typeparam name="T">The enum.</typeparam>
    /// <param name="value">The list.</param>
    /// <returns>The values, or null when the list was left out.</returns>
    internal static IReadOnlyList<T>? ParseList<T>(string? value)
        where T : struct, Enum =>
        value is null
            ? null
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => Enum.TryParse<T>(name, true, out var parsed) ? (T?)parsed : null)
                .OfType<T>()
                .ToList();

    /// <summary>
    /// Formats section timings as a Server-Timing header value.
    /// </summary>
    /// <param name="timings">The timings.</param>
    /// <returns>The header value.</returns>
    internal static string ServerTiming(IEnumerable<SectionTiming> timings) =>
        string.Join(", ", timings.Select(t => string.Create(
            CultureInfo.InvariantCulture,
            $"{t.SectionId};desc=\"{(t.Hit ? "hit" : "miss")}\";dur={t.Milliseconds:0.0}")));

    private static ItemShape Shape(string? fields, string? imageTypes, int? imageTypeLimit, bool? enableUserData) =>
        new(ParseList<ItemFields>(fields), ParseList<ImageType>(imageTypes), imageTypeLimit, enableUserData);

    private User? SignedInUser() =>
        Guid.TryParse(User.FindFirst(UserIdClaim)?.Value, out var id) && !id.Equals(Guid.Empty) ? _users.GetUserById(id) : null;

    private void SetServerTiming(IEnumerable<SectionTiming> timings)
    {
        var value = ServerTiming(timings);
        if (value.Length > 0)
        {
            Response.Headers["Server-Timing"] = value;
        }
    }

    // The response's own entity tag: a client that already has this exact answer gets 304.
    private ActionResult WithETag<T>(T body)
    {
        var etag = "\"" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(body)))[..16] + "\"";
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "private, no-cache";
        var sent = Request.Headers.IfNoneMatch.SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.TrimEntries));
        return sent.Any(tag => tag == etag || tag == "*") ? StatusCode(StatusCodes.Status304NotModified) : Ok(body);
    }
}
