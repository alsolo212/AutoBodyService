using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using AutoBodyService.Application.Appointments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutoBodyService.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/admin/repair-capacity")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class RepairCapacityController(IRepairCapacity capacity) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Overview([FromQuery] DateOnly? date, CancellationToken cancellationToken)
    {
        if (date is null || date.Value.Year is < 2 or > 9998) return BadRequest();
        return Ok(await capacity.OverviewAsync(date.Value, cancellationToken));
    }

    [HttpPut]
    public async Task<IActionResult> SetWorkplaces(RepairCapacityRequest request, CancellationToken cancellationToken)
    {
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actor is null) return Unauthorized();
        if (request.Workplaces is null or <= 0) return BadRequest();
        return await capacity.SetWorkplacesAsync(request.Workplaces.Value, actor, cancellationToken)
            ? NoContent() : BadRequest();
    }

    [HttpGet("preview")]
    public async Task<IActionResult> Preview([FromQuery] DateOnly? start, [FromQuery] int? durationDays, CancellationToken cancellationToken)
    {
        if (start is null || durationDays is null) return BadRequest();
        var result = await capacity.PreviewAsync(start.Value, durationDays.Value, cancellationToken);
        return result is null ? BadRequest(new ProblemDetails { Title = "Перевірте дату та тривалість.", Status = 400 }) : Ok(result);
    }
}

/// <summary>Number of physical repair workplaces.</summary>
public sealed record RepairCapacityRequest([Required] int? Workplaces);
