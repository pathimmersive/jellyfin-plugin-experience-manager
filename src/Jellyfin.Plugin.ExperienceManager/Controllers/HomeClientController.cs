using System;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ExperienceManager.Controllers;

/// <summary>
/// Serves the home client's script and stylesheet that jellyfin-web loads.
/// </summary>
[ApiController]
[Route(HomeClient.Route)]
public class HomeClientController : ControllerBase
{
    private readonly HomeClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="HomeClientController"/> class.
    /// </summary>
    /// <param name="client">The embedded home client files.</param>
    public HomeClientController(HomeClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Serves the home client script. Anonymous, because a script tag cannot attach an Authorization
    /// header, and the script holds no user data. Clients revalidate it on every load, so a rebuilt
    /// plugin reaches them even when its version is unchanged.
    /// </summary>
    /// <returns>The script, or 304 when the client already has it.</returns>
    [HttpGet(HomeClient.ScriptFileName)]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public ActionResult GetScript() => Serve(_client.ScriptETag, _client.OpenScript, HomeClient.ScriptContentType);

    /// <summary>
    /// Serves the home client stylesheet. Anonymous and revalidated on every load, like the script.
    /// </summary>
    /// <returns>The stylesheet, or 304 when the client already has it.</returns>
    [HttpGet(HomeClient.StyleFileName)]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public ActionResult GetStyle() => Serve(_client.StyleETag, _client.OpenStyle, HomeClient.StyleContentType);

    /// <summary>
    /// Gets the admin page's Programming script. The routes it calls are for administrators only.
    /// </summary>
    /// <returns>The script, or 304 when the client already has it.</returns>
    [HttpGet(HomeClient.AdminScriptFileName)]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public ActionResult GetAdminScript() => Serve(_client.AdminScriptETag, _client.OpenAdminScript, HomeClient.ScriptContentType);

    /// <summary>
    /// Gets the admin page's Audiences script. The routes it calls are for administrators only.
    /// </summary>
    /// <returns>The script, or 304 when the client already has it.</returns>
    [HttpGet(HomeClient.AudiencesScriptFileName)]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public ActionResult GetAudiencesScript() => Serve(_client.AudiencesScriptETag, _client.OpenAudiencesScript, HomeClient.ScriptContentType);

    /// <summary>
    /// Gets the admin page's Home layout script. The routes it calls are for administrators only.
    /// </summary>
    /// <returns>The script, or 304 when the client already has it.</returns>
    [HttpGet(HomeClient.LayoutScriptFileName)]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public ActionResult GetLayoutScript() => Serve(_client.LayoutScriptETag, _client.OpenLayoutScript, HomeClient.ScriptContentType);

    /// <summary>
    /// Gets the admin page's Content rules script. The routes it calls are for administrators only.
    /// </summary>
    /// <returns>The script, or 304 when the client already has it.</returns>
    [HttpGet(HomeClient.RulesScriptFileName)]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public ActionResult GetRulesScript() => Serve(_client.RulesScriptETag, _client.OpenRulesScript, HomeClient.ScriptContentType);

    private ActionResult Serve(string etag, Func<Stream> open, string contentType)
    {
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.ETag = etag;
        if (ClientHas(etag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return File(open(), contentType);
    }

    // If-None-Match may list several entity tags, in one header or several.
    private bool ClientHas(string etag) =>
        Request.Headers.IfNoneMatch
            .SelectMany(value => (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries))
            .Any(candidate => candidate == etag || candidate == "*");
}
