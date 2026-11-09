using System.ComponentModel.DataAnnotations;
using AutoBodyService.Application.InspectionSchedule;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutoBodyService.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/admin/inspection-calendar")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class InspectionCalendarController(IInspectionCalendarService calendar) : ControllerBase
{
    /// <summary>Read inspections on a Kyiv calendar date, including previously booked slots.</summary>
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery, Required] DateOnly? date, CancellationToken cancellationToken)
    {
        if (date is null || date.Value.Year is < 2 or > 9998) return BadRequest();
        return Ok(await calendar.GetDayAsync(date.Value, cancellationToken));
    }
}
