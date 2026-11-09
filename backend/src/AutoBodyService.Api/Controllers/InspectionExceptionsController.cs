using System.ComponentModel.DataAnnotations;
using AutoBodyService.Application.InspectionSchedule;
using AutoBodyService.Domain.InspectionSchedule;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutoBodyService.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/admin/inspection-exceptions")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class InspectionExceptionsController(IInspectionExceptionsService service, IInspectionCalendarService calendar) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery, Required] DateOnly? date, CancellationToken cancellationToken)
    {
        if (date is null || !ValidDate(date.Value)) return BadRequest();
        var schedule = await service.GetAsync(date.Value, cancellationToken);
        return Ok(new InspectionExceptionsResponse(schedule.Version, "Europe/Kyiv", schedule.Date, schedule.Exception, schedule.TemplateSlots));
    }

    [HttpPost("preview")]
    public Task<IActionResult> Preview(UpdateInspectionExceptionRequest request, CancellationToken cancellationToken) => Change(request, true, cancellationToken);

    [HttpPut]
    public Task<IActionResult> Put(UpdateInspectionExceptionRequest request, CancellationToken cancellationToken) => Change(request, false, cancellationToken);

    private async Task<IActionResult> Change(UpdateInspectionExceptionRequest request, bool preview, CancellationToken cancellationToken)
    {
        if (!ValidDate(request.Date) || (request.Exception is not null &&
            (request.Exception.Mode is null || request.Exception.Intervals is null || request.Exception.Slots is null ||
             request.Exception.Intervals.Any(x => x is null || x.Start is null || x.End is null) ||
             request.Exception.Slots.Any(x => x is null || x.Start is null || x.Mode is null)))) return BadRequest();
        var item = request.Exception;
        var exception = item is null ? null : new InspectionException(request.Date, item.Mode!.Value, item.Reason,
            item.Intervals!.Select(x => new DateInterval(x!.Start!.Value, x.End!.Value)).ToArray(),
            item.Slots!.Select(x => new SlotOverride(x!.Start!.Value, x.Mode!.Value)).ToArray());
        var result = await calendar.ChangeDateAsync(request.Version, request.Date, exception, preview, request.ConfirmationToken, cancellationToken);
        return result.Status switch
        {
            ScheduleUpdateResult.Success when preview => Ok(result.Preview),
            ScheduleUpdateResult.Success => Ok(result.Preview),
            ScheduleUpdateResult.Invalid => BadRequest(new ProblemDetails { Status = 400, Title = "Некоректне виключення графіка." }),
            _ => Conflict(new ProblemDetails { Status = 409, Title = "Графік або записи змінилися. Повторіть перевірку." })
        };
    }

    private static bool ValidDate(DateOnly date) => date.Year is >= 2 and <= 9998;
}

/// <summary>Effective schedule for a Kyiv calendar date.</summary>
public sealed record InspectionExceptionsResponse(Guid Version, string TimeZone, DateOnly Date, InspectionException? Exception, IReadOnlyList<EffectiveSlot> TemplateSlots);
/// <summary>Replace or remove the exception for a local date using the current schedule version.</summary>
public sealed record UpdateInspectionExceptionRequest
{
    [Required] public required Guid Version { get; init; }
    [Required] public required DateOnly Date { get; init; }
    public required InspectionExceptionRequest? Exception { get; init; }
    public string? ConfirmationToken { get; init; }
}
/// <summary>Date-level rule and individual half-hour slot rules.</summary>
public sealed record InspectionExceptionRequest(DateOverrideMode? Mode, string? Reason, List<DateIntervalRequest?>? Intervals, List<SlotOverrideRequest?>? Slots);
/// <summary>A local interval on the selected date.</summary>
public sealed record DateIntervalRequest(TimeOnly? Start, TimeOnly? End);
/// <summary>A local slot to open or close.</summary>
public sealed record SlotOverrideRequest(TimeOnly? Start, SlotOverrideMode? Mode);
