using Jellyfin.Plugin.ExperienceManager.Native;
using MediaBrowser.Controller.Devices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ExperienceManager.Controllers;

/// <summary>
/// The admin page's own routes. Administrators only; not part of the published home API.
/// </summary>
[ApiController]
[Route(Route)]
[Authorize(Policy = "RequiresElevation")]
public class AdminController : ControllerBase
{
    /// <summary>The route prefix every admin route shares.</summary>
    public const string Route = HomeClient.Route + "/Admin";

    private readonly IDeviceManager _devices;
    private readonly ReachRegistry _reach;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminController"/> class.
    /// </summary>
    /// <param name="devices">The device manager.</param>
    /// <param name="reach">The reach adapters.</param>
    public AdminController(IDeviceManager devices, ReachRegistry reach)
    {
        _devices = devices;
        _reach = reach;
    }

    /// <summary>
    /// Gets the client census: every registered device by user and client family, what Experience Manager reaches on each
    /// family, and every reach adapter with its Detect status.
    /// </summary>
    /// <returns>The census.</returns>
    [HttpGet("Clients")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ClientCensusResponse> GetClients() => ClientCensus.Build(_devices.GetDevicesForUser(null).Items, _reach.Statuses());
}
