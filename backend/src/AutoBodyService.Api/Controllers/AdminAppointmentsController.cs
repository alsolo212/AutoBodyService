using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json.Serialization;
using AutoBodyService.Application.Appointments;
using AutoBodyService.Domain.Appointments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutoBodyService.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/admin/appointments")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminAppointmentsController(IAdminAppointmentCommands commands) : ControllerBase
{
    [HttpPut("{id:guid}/repair-plan")]
    public async Task<IActionResult> PutPlan(Guid id, RepairPlanRequest request, CancellationToken cancellationToken)
    {
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actor is null) return Unauthorized();
        var command = new RepairPlanCommand(request.Price!.Value, request.PlannedStartUtc!.Value, request.EstimatedFinishUtc!.Value, request.InspectionPerformed == true);
        return Result(await commands.PutPlanAsync(id, request.Version!.Value, actor, command, cancellationToken));
    }

    [HttpPost("{id:guid}/transition")]
    public async Task<IActionResult> Transition(Guid id, TransitionRequest request, CancellationToken cancellationToken)
    {
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actor is null) return Unauthorized();
        var command = new TransitionCommand(request.Target!.Value, request.InspectionPerformed == true, request.PaymentReceived == true, request.VehicleHandedOver == true);
        return Result(await commands.TransitionAsync(id, request.Version!.Value, actor, command, cancellationToken));
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, AdminCancelRequest request, CancellationToken cancellationToken)
    {
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actor is null) return Unauthorized();
        return Result(await commands.CancelAsync(id, request.Version!.Value, actor, request.Confirmed == true, request.Reason, cancellationToken));
    }

    private IActionResult Result(AdminAppointmentResult result) => result switch
    {
        AdminAppointmentResult.Success => NoContent(),
        AdminAppointmentResult.NotFound => NotFound(new ProblemDetails { Status = 404, Title = "Замовлення не знайдено." }),
        AdminAppointmentResult.Invalid => BadRequest(new ProblemDetails { Status = 400, Title = "Перевірте дані замовлення." }),
        _ => Conflict(new ProblemDetails { Status = 409, Title = "Замовлення змінилося або дія недоступна." })
    };
}

/// <summary>Agreed repair terms and the version returned by the existing administrative details endpoint.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RepairPlanRequest
{
    [Required] public required uint? Version { get; init; }
    [Required] public required decimal? Price { get; init; }
    [Required] public required DateTimeOffset? PlannedStartUtc { get; init; }
    [Required] public required DateTimeOffset? EstimatedFinishUtc { get; init; }
    public bool? InspectionPerformed { get; init; }
}

/// <summary>One forward transition, with explicit facts where required.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TransitionRequest
{
    [Required] public required uint? Version { get; init; }
    [Required] public required AppointmentStatus? Target { get; init; }
    public bool? InspectionPerformed { get; init; }
    public bool? PaymentReceived { get; init; }
    public bool? VehicleHandedOver { get; init; }
}

/// <summary>An explicit administrative cancellation and its reason.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AdminCancelRequest
{
    [Required] public required uint? Version { get; init; }
    [Required] public required bool? Confirmed { get; init; }
    [Required, StringLength(500)] public required string Reason { get; init; }
}
