using System.ComponentModel.DataAnnotations;
using AutoBodyService.Application.InspectionSchedule;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutoBodyService.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/admin/inspection-schedule")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class InspectionScheduleController(IInspectionScheduleService schedules, IInspectionCalendarService calendar) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<InspectionScheduleResponse>> Get(CancellationToken cancellationToken)
    {
        var schedule = await schedules.GetAsync(cancellationToken);
        return Ok(new InspectionScheduleResponse(schedule.Version, "Europe/Kyiv", schedule.Intervals));
    }

    [HttpPost("preview")]
    public Task<IActionResult> Preview(UpdateInspectionScheduleRequest request, CancellationToken cancellationToken) => Change(request, true, cancellationToken);

    [HttpPut]
    public Task<IActionResult> Put(UpdateInspectionScheduleRequest request, CancellationToken cancellationToken) => Change(request, false, cancellationToken);

    private async Task<IActionResult> Change(UpdateInspectionScheduleRequest request, bool preview, CancellationToken cancellationToken)
    {
        if (request.Intervals.Any(i => i is null || i.DayOfWeek is null || i.Start is null || i.End is null))
            return BadRequest(new ProblemDetails { Status = 400, Title = "Некоректний інтервал графіка." });
        var intervals = request.Intervals.Select(i => new WeeklyInterval(i!.DayOfWeek!.Value, i.Start!.Value, i.End!.Value)).ToArray();
        var result = await calendar.ChangeWeeklyAsync(request.Version, intervals, preview, request.ConfirmationToken, cancellationToken);
        return result.Status switch
        {
            ScheduleUpdateResult.Success when preview => Ok(result.Preview),
            ScheduleUpdateResult.Success => NoContent(),
            ScheduleUpdateResult.Invalid => BadRequest(new ProblemDetails { Status = 400, Title = "Некоректний інтервал графіка." }),
            _ => Conflict(new ProblemDetails { Status = 409, Title = "Графік або записи змінилися. Повторіть перевірку." })
        };
    }
}

/// <summary>Weekly local inspection hours and the version required for updates.</summary>
public sealed record InspectionScheduleResponse(Guid Version, string TimeZone, IReadOnlyList<WeeklyInterval> Intervals);
/// <summary>Complete replacement for the weekly schedule.</summary>
public sealed record UpdateInspectionScheduleRequest
{
    [Required] public required Guid Version { get; init; }
    [Required, MaxLength(336)] public required List<InspectionIntervalRequest?> Intervals { get; init; }
    public string? ConfirmationToken { get; init; }
}
/// <summary>A local interval on one day of the week.</summary>
public sealed record InspectionIntervalRequest(DayOfWeek? DayOfWeek, TimeOnly? Start, TimeOnly? End);
