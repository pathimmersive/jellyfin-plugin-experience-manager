using System.Threading.Tasks;
using Jellyfin.Plugin.ExperienceManager.Announcements;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ExperienceManager.Controllers;

/// <summary>
/// An uploaded image: its id, for an announcement's <c>Image</c>, and where clients get it.
/// </summary>
/// <param name="Id">The image id.</param>
/// <param name="Path">Its path on the server.</param>
public sealed record AdminImageResponse(string Id, string Path);

/// <summary>
/// Uploads images for announcements. For administrators only.
/// </summary>
[ApiController]
[Route(AdminController.Route + "/Images")]
[Authorize(Policy = "RequiresElevation")]
public class AdminImagesController : ControllerBase
{
    private readonly AnnouncementImages _images;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminImagesController"/> class.
    /// </summary>
    /// <param name="images">The uploaded images.</param>
    public AdminImagesController(AnnouncementImages images)
    {
        _images = images;
    }

    /// <summary>
    /// Stores an image sent as the request body: a PNG, JPEG or WebP image of at most 5 MB, judged by its content.
    /// </summary>
    /// <returns>The image's id and path, or 400 with the reason.</returns>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AdminImageResponse>> Upload()
    {
        // The body is read in full before it is judged; Kestrel reads it asynchronously.
        using var body = new System.IO.MemoryStream();
        var limited = new byte[81920];
        int read;
        while ((read = await Request.Body.ReadAsync(limited).ConfigureAwait(false)) > 0)
        {
            body.Write(limited, 0, read);
            if (body.Length > AnnouncementImages.MaxBytes)
            {
                return BadRequest(new { Error = "The image is larger than 5 MB." });
            }
        }

        body.Position = 0;
        var error = _images.Save(body, out var id);
        if (error is not null)
        {
            return BadRequest(new { Error = error });
        }

        return new AdminImageResponse(id!, "/ExperienceManager/v1/Images/" + id);
    }
}
