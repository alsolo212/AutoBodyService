using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json.Serialization;
using AutoBodyService.Application.Assignments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutoBodyService.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/admin/assignments")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminAssignmentsController(IEmployeeAssignments assignments) : ControllerBase
{
    /// <summary>Read assignment history, optionally filtered by order, worker, and activity.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AssignmentDetails>>> List([FromQuery] Guid? appointmentId, [FromQuery] Guid? employeeId,
        [FromQuery] bool? active, CancellationToken cancellationToken)
    {
        if (appointmentId == Guid.Empty || employeeId == Guid.Empty) return BadRequest(new ProblemDetails { Status = 400, Title = "Некоректний фільтр." });
        return Ok(await assignments.ListAsync(appointmentId, employeeId, active, cancellationToken));
    }

    /// <summary>Read one assignment record.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AssignmentDetails>> Get(Guid id, CancellationToken cancellationToken)
    {
        var assignment = await assignments.GetAsync(id, cancellationToken);
        return assignment is null ? NotFound() : Ok(assignment);
    }

    /// <summary>Assign an active worker to a repair in progress.</summary>
    [HttpPost]
    public async Task<ActionResult<AssignmentDetails>> Create(CreateAssignmentRequest request, CancellationToken cancellationToken)
    {
        if (request.AppointmentId == Guid.Empty || request.EmployeeId == Guid.Empty) return BadRequest(new ProblemDetails { Status = 400, Title = "Вкажіть замовлення та працівника." });
        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actorId is null) return Unauthorized();
        var (result, assignment) = await assignments.AssignAsync(request.AppointmentId!.Value, request.EmployeeId!.Value, actorId, cancellationToken);
        return result switch
        {
            AssignmentResult.Success => CreatedAtAction(nameof(Get), new { id = assignment!.Id }, assignment),
            AssignmentResult.NotFound => NotFound(),
            _ => Conflict(new ProblemDetails { Status = 409, Title = "Призначення недоступне або вже існує." })
        };
    }

    /// <summary>Close one assignment while keeping its history.</summary>
    [HttpPatch("{id:guid}/release")]
    public async Task<IActionResult> Release(Guid id, CancellationToken cancellationToken)
    {
        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actorId is null) return Unauthorized();
        return await assignments.ReleaseAsync(id, actorId, cancellationToken) switch
        {
            AssignmentResult.Success => NoContent(),
            AssignmentResult.NotFound => NotFound(),
            _ => Conflict(new ProblemDetails { Status = 409, Title = "Призначення вже завершено." })
        };
    }
}

/// <summary>Order and worker to associate. Actor and time come from the server.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateAssignmentRequest
{
    [Required] public Guid? AppointmentId { get; init; }
    [Required] public Guid? EmployeeId { get; init; }
}
