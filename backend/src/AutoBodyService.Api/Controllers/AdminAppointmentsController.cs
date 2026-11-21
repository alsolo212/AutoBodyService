using AutoBodyService.Api.Validation;
using System.Text.Json;
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
public sealed class AdminAppointmentsController(IAdminAppointmentCommands commands, IAdminOverviewQueries overview, IManualContactService contacts, ICreateAppointmentService creation) : ControllerBase
{
    /// <summary>Create an inspection for a customer without an account.</summary>
    [HttpPost("guest")]
    public async Task<IActionResult> CreateGuest(CreateGuestAppointmentRequest request, CancellationToken cancellationToken)
    {
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actor is null) return Unauthorized();
        var result = await creation.CreateAsync(new(actor, request.InspectionStartUtc!.Value, request.Vin, request.Make,
            request.Model, request.ProblemDescription, request.CustomerName, request.CustomerPhone), cancellationToken);
        return result.Status switch
        {
            CreateAppointmentStatus.Created => Created($"/api/appointments/{result.Id}", new AppointmentCreatedResponse(result.Id!.Value, result.InspectionStartUtc!.Value, "AwaitingInspection")),
            CreateAppointmentStatus.Invalid => BadRequest(new ProblemDetails { Status = 400, Title = "Перевірте дані запису." }),
            CreateAppointmentStatus.Unauthorized => Forbid(),
            _ => Conflict(new ProblemDetails { Status = 409, Title = "Обраний час недоступний. Оберіть інший слот." })
        };
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] AppointmentStatus? status, [FromQuery] string? search,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? sort,
        [FromQuery] string? direction, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        sort = (sort ?? "inspection").ToLowerInvariant();
        direction = (direction ?? "asc").ToLowerInvariant();
        if ((status.HasValue && !Enum.IsDefined(status.Value)) || search?.Length > 100 ||
            sort is not ("inspection" or "created" or "customer") || direction is not ("asc" or "desc") ||
            page is < 1 or > 1000000 || pageSize is < 1 or > 100 ||
            (from.HasValue && from.Value.Year is < 2 or > 9998) || (to.HasValue && to.Value.Year is < 2 or > 9998) ||
            (from.HasValue && to.HasValue && from.Value > to.Value))
            return BadRequest(new ProblemDetails { Status = 400, Title = "Некоректні параметри пошуку." });
        return Ok(await overview.GetAppointmentsAsync(new(status, search, from, to, sort, direction == "desc", page, pageSize), cancellationToken));
    }

    [HttpPost("{id:guid}/manual-contact")]
    public async Task<IActionResult> RecordManualContact(Guid id, ManualContactRequest request, CancellationToken cancellationToken)
    {
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actor is null) return Unauthorized();
        var result = await contacts.RecordAsync(id, request.Reason!.Value, actor, request.Version!.Value, request.CustomerPhone, request.CustomerName, cancellationToken);
        return result switch
        {
            ManualContactResult.Recorded => NoContent(),
            ManualContactResult.NotFound => NotFound(new ProblemDetails { Status = 404, Title = "Замовлення не знайдено." }),
            ManualContactResult.Conflict => Conflict(new ProblemDetails { Status = 409, Title = "Замовлення завершено або скасовано." }),
            _ => BadRequest(new ProblemDetails { Status = 400, Title = "Некоректний привід зв'язку." })
        };
    }

    [HttpPut("{id:guid}/repair-plan")]
    public async Task<IActionResult> PutPlan(Guid id, RepairPlanRequest request, CancellationToken cancellationToken)
    {
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actor is null) return Unauthorized();
        if (!ExactDecimal.TryNonnegative(request.Price, out var price))
            return BadRequest(new ProblemDetails { Status = 400, Title = "Вкажіть точну невід’ємну вартість ремонту." });
        var command = new RepairPlanCommand(price, request.PlannedStartUtc!.Value, request.EstimatedFinishUtc!.Value, request.InspectionPerformed == true, request.CustomerAgreed == true);
        return Result(await commands.PutPlanAsync(id, request.Version!.Value, actor, command, cancellationToken));
    }

    /// <summary>Agree a repair plan and schedule the order in one transaction.</summary>
    [HttpPost("{id:guid}/schedule")]
    public async Task<IActionResult> Schedule(Guid id, ScheduleRepairRequest request, CancellationToken cancellationToken)
    {
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actor is null) return Unauthorized();
        if (!ExactDecimal.TryNonnegative(request.Price, out var price)) return BadRequest();
        return Result(await commands.ScheduleAsync(id, request.Version!.Value, actor,
            new(request.Start!.Value, request.DurationDays!.Value, price, request.InspectionPerformed == true), cancellationToken));
    }

    [HttpPost("{id:guid}/transition")]
    public async Task<IActionResult> Transition(Guid id, TransitionRequest request, CancellationToken cancellationToken)
    {
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actor is null) return Unauthorized();
        var command = new TransitionCommand(request.Target!.Value, request.InspectionPerformed == true, request.PaymentReceived == true, request.VehicleHandedOver == true);
        return Result(await commands.TransitionAsync(id, request.Version!.Value, actor, command, cancellationToken));
    }

    /// <summary>Pause a repair and record its expected return and workspace state.</summary>
    [HttpPost("{id:guid}/pause")]
    public async Task<IActionResult> Pause(Guid id, PauseRepairRequest request, CancellationToken cancellationToken)
    {
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actor is null) return Unauthorized();
        return Result(await commands.PauseAsync(id, request.Version!.Value, actor,
            new(request.Reason, request.DurationDays!.Value, request.WorkplaceReleased!.Value), cancellationToken));
    }

    /// <summary>Resume a paused repair after checking actual occupancy.</summary>
    [HttpPost("{id:guid}/resume")]
    public async Task<IActionResult> Resume(Guid id, ResumeRepairRequest request, CancellationToken cancellationToken)
    {
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actor is null) return Unauthorized();
        return Result(await commands.ResumeAsync(id, request.Version!.Value, actor, cancellationToken));
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

/// <summary>Customer contact and inspection details supplied by an administrator.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateGuestAppointmentRequest
{
    [Required, StringLength(200)] public required string CustomerName { get; init; }
    [Required, StringLength(32)] public required string CustomerPhone { get; init; }
    [Required] public required DateTimeOffset? InspectionStartUtc { get; init; }
    [StringLength(19)] public string? Vin { get; init; }
    [Required, StringLength(200)] public required string Make { get; init; }
    [Required, StringLength(200)] public required string Model { get; init; }
    [Required, StringLength(5000)] public required string ProblemDescription { get; init; }
}

/// <summary>Records a manual interaction without claiming message delivery.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ManualContactRequest
{
    [Required] public required ManualContactReason? Reason { get; init; }
    [Required] public required uint? Version { get; init; }
    [Required, StringLength(32)] public required string CustomerPhone { get; init; }
    [Required, StringLength(200)] public required string CustomerName { get; init; }
}

/// <summary>Agreed repair terms and the version returned by the existing administrative details endpoint.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RepairPlanRequest
{
    [Required] public required uint? Version { get; init; }
    public JsonElement Price { get; init; }
    [Required] public required DateTimeOffset? PlannedStartUtc { get; init; }
    [Required] public required DateTimeOffset? EstimatedFinishUtc { get; init; }
    public bool? InspectionPerformed { get; init; }
    public bool? CustomerAgreed { get; init; }
}

/// <summary>Calendar dates and agreed price for the initial repair plan.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScheduleRepairRequest
{
    [Required] public required uint? Version { get; init; }
    [Required] public required DateOnly? Start { get; init; }
    [Required] public required int? DurationDays { get; init; }
    public JsonElement Price { get; init; }
    [Required] public required bool? InspectionPerformed { get; init; }
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

/// <summary>Required facts about a temporary interruption.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PauseRepairRequest
{
    [Required] public required uint? Version { get; init; }
    [Required, StringLength(500)] public required string Reason { get; init; }
    [Required] public required int? DurationDays { get; init; }
    [Required] public required bool? WorkplaceReleased { get; init; }
}

/// <summary>Version of the paused order to resume.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ResumeRepairRequest([Required] uint? Version);

/// <summary>An explicit administrative cancellation and its reason.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AdminCancelRequest
{
    [Required] public required uint? Version { get; init; }
    [Required] public required bool? Confirmed { get; init; }
    [Required, StringLength(500)] public required string Reason { get; init; }
}
