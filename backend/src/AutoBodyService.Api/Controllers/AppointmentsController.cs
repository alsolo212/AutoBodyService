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
public sealed class AppointmentsController(ICreateAppointmentService service, UserManager<ApplicationUser> users) : ControllerBase
{
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
