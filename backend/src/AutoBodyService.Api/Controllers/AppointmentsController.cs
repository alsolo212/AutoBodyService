using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using AutoBodyService.Application.Appointments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using AutoBodyService.Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc;

namespace AutoBodyService.Api.Controllers;

[ApiController]
[Authorize(Policy = "User")]
[Route("api/appointments")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AppointmentsController(ICreateAppointmentService service, IAppointmentQueries queries, ICancelAppointmentService cancellations, UserManager<ApplicationUser> users) : ControllerBase
{
    /// <summary>List only the current account's appointments, optionally by category.</summary>
    [HttpGet("my")]
    [ProducesResponseType(typeof(IReadOnlyList<AppointmentSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Mine([FromQuery] string? category, CancellationToken cancellationToken)
    {
        var owner = users.GetUserId(User);
        if (owner is null) return Unauthorized();
        AppointmentCategory? selected = category?.ToLowerInvariant() switch
        {
            null => null,
            "active" => AppointmentCategory.Active,
            "completed" => AppointmentCategory.Completed,
            "cancelled" => AppointmentCategory.Cancelled,
            _ => (AppointmentCategory)(-1)
        };
        if (selected == (AppointmentCategory)(-1)) return BadRequest(new ProblemDetails { Status = 400, Title = "Некоректний фільтр записів." });
        return Ok(await queries.GetMineAsync(owner, selected, cancellationToken));
    }

    /// <summary>Get details available to the owner or to an administrator.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken)
    {
        var owner = users.GetUserId(User);
        if (owner is null) return Unauthorized();
        if (User.IsInRole(ApplicationRoles.Admin) || User.IsInRole(ApplicationRoles.SuperAdmin))
        {
            var details = await queries.GetForAdminAsync(id, cancellationToken);
            return details is null ? NotFound() : Ok(details);
        }
        var own = await queries.GetOwnAsync(id, owner, cancellationToken);
        return own is null ? NotFound() : Ok(own);
    }

    /// <summary>Cancel the current account's future inspection.</summary>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
    {
        var owner = users.GetUserId(User);
        if (owner is null) return Unauthorized();
        return await cancellations.CancelOwnAsync(id, owner, cancellationToken) switch
        {
            CancelAppointmentResult.Cancelled => NoContent(),
            CancelAppointmentResult.NotFound => NotFound(),
            _ => Conflict(new ProblemDetails { Status = 409, Title = "Запис неможливо скасувати." })
        };
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateAppointmentRequest request, CancellationToken cancellationToken)
    {
        var owner = users.GetUserId(User);
        if (owner is null) return Unauthorized();
        var result = await service.CreateAsync(new(owner, request.InspectionStartUtc!.Value, request.Vin, request.Make, request.Model, request.ProblemDescription), cancellationToken);
        return result.Status switch
        {
            CreateAppointmentStatus.Created => Created($"/api/appointments/{result.Id}", new AppointmentCreatedResponse(result.Id!.Value, result.InspectionStartUtc!.Value, "AwaitingInspection")),
            CreateAppointmentStatus.Invalid => BadRequest(new ProblemDetails { Status = 400, Title = "Перевірте дані запису." }),
            CreateAppointmentStatus.Unauthorized => Unauthorized(),
            _ => Conflict(new ProblemDetails { Status = 409, Title = "Обраний час недоступний. Оберіть інший слот." })
        };
    }
}

/// <summary>Inspection details for the current account.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateAppointmentRequest
{
    [Required] public required DateTimeOffset? InspectionStartUtc { get; init; }
    [Required, StringLength(19)] public required string Vin { get; init; }
    [Required, StringLength(200)] public required string Make { get; init; }
    [Required, StringLength(200)] public required string Model { get; init; }
    [Required, StringLength(5000)] public required string ProblemDescription { get; init; }
}
/// <summary>Confirmation of the newly created inspection.</summary>
public sealed record AppointmentCreatedResponse(Guid Id, DateTimeOffset InspectionStartUtc, string Status);
