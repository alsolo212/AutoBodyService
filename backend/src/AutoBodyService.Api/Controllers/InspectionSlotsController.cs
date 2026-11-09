using System.ComponentModel.DataAnnotations;
using AutoBodyService.Application.InspectionSchedule;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutoBodyService.Api.Controllers;

[ApiController]
[Authorize(Policy = "User")]
[Route("api/inspection-slots")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class InspectionSlotsController(IInspectionSlotsService slots) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery, Required] DateOnly? date, CancellationToken cancellationToken)
    {
        if (date is null || date.Value.Year is < 2 or > 9998) return BadRequest();
        return Ok(new InspectionSlotsResponse(date.Value, "Europe/Kyiv", await slots.GetAvailableAsync(date.Value, cancellationToken)));
    }
}

/// <summary>Available half-hour slots for a Kyiv calendar date, expressed in UTC.</summary>
public sealed record InspectionSlotsResponse(DateOnly Date, string TimeZone, IReadOnlyList<InspectionSlot> Slots);
