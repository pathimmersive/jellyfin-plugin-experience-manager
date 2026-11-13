using Jellyfin.Plugin.ExperienceManager.Health;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ExperienceManager.Controllers;

/// <summary>
/// The health checks, for Operate > Health and performance and the badges. For administrators only.
/// </summary>
[ApiController]
[Route(AdminController.Route + "/Health")]
[Authorize(Policy = "RequiresElevation")]
public class AdminHealthController : ControllerBase
{
    private readonly HealthChecks _checks;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminHealthController"/> class.
    /// </summary>
    /// <param name="checks">The health checks.</param>
    public AdminHealthController(HealthChecks checks)
    {
        _checks = checks;
    }

    /// <summary>
    /// Gets every finding and the slowest sections, from the last minute's run unless asked to run again.
    /// </summary>
    /// <param name="refresh">Whether to run the checks now.</param>
    /// <returns>The report.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<HealthReport> GetHealth([FromQuery] bool refresh = false) => _checks.Report(refresh);
}
