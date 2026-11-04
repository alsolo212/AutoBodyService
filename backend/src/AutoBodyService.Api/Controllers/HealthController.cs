using AutoBodyService.Api.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace AutoBodyService.Api.Controllers;

[ApiController]
[Route("api/health")]
public sealed class HealthController : ControllerBase
{
    /// <summary>
    /// Returns a technical availability status for the API.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<HealthStatusResponse>(StatusCodes.Status200OK)]
    public ActionResult<HealthStatusResponse> Get(CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        return Ok(new HealthStatusResponse("Healthy"));
    }
}
