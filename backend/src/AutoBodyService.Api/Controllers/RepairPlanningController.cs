using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json.Serialization;
using AutoBodyService.Application.Appointments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutoBodyService.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/admin/repair-planning")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class RepairPlanningController(IRepairPlanning planning, IAdminAppointmentCommands commands) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Overview(CancellationToken cancellationToken) => Ok(await planning.OverviewAsync(cancellationToken));

    [HttpGet("pause/{pauseId:guid}/offer")]
    public async Task<IActionResult> PauseOffer(Guid pauseId, CancellationToken cancellationToken)
    {
        var offer = await planning.PauseOfferAsync(pauseId, cancellationToken);
        return offer is null ? NotFound() : Ok(offer);
    }

    [HttpPost("pause/{pauseId:guid}/decline")]
    public async Task<IActionResult> Decline(Guid pauseId, DeclineOfferRequest request, CancellationToken cancellationToken)
    {
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier); if (actor is null) return Unauthorized();
        return Result(await planning.DeclinePauseOfferAsync(pauseId, request.CandidateId!.Value, actor, cancellationToken));
    }

    [HttpPost("{id:guid}/accept-earlier")]
    public async Task<IActionResult> AcceptEarlier(Guid id, AcceptEarlierRequest request, CancellationToken cancellationToken)
    {
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier); if (actor is null) return Unauthorized();
        if (request.CustomerAgreed != true) return BadRequest();
        return Result(await commands.AcceptEarlierAsync(id, request.Version!.Value, actor, request.PauseId, cancellationToken));
    }

    [HttpGet("{id:guid}/delay-preview")]
    public async Task<IActionResult> DelayPreview(Guid id, [FromQuery] DateOnly? finish, CancellationToken cancellationToken)
    {
        if (finish is null) return BadRequest();
        var result = await planning.PreviewDelayAsync(id, finish.Value, cancellationToken);
        return result is null ? BadRequest() : Ok(result);
    }

    [HttpPost("{id:guid}/delay")]
    public async Task<IActionResult> Delay(Guid id, DelayRepairRequest request, CancellationToken cancellationToken)
    {
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier); if (actor is null) return Unauthorized();
        if (request.Confirmed != true) return BadRequest();
        return Result(await commands.DelayAsync(id, request.Version!.Value, actor, request.Finish!.Value, request.ConfirmationToken, cancellationToken));
    }

    private IActionResult Result(AdminAppointmentResult result) => result switch
    {
        AdminAppointmentResult.Success => NoContent(),
        AdminAppointmentResult.NotFound => NotFound(),
        AdminAppointmentResult.Invalid => BadRequest(new ProblemDetails { Title = "Перевірте дані.", Status = 400 }),
        _ => Conflict(new ProblemDetails { Title = "Плани змінилися. Оновіть дані та перевірте знову.", Status = 409 })
    };
}

/// <summary>The candidate whose customer declined the current offer.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeclineOfferRequest([Required] Guid? CandidateId);
/// <summary>Explicit agreement to move a candidate earlier.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AcceptEarlierRequest([Required] uint? Version, Guid? PauseId, [Required] bool? CustomerAgreed);
/// <summary>Confirmed delay against the reviewed planning snapshot.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DelayRepairRequest([Required] uint? Version, [Required] DateOnly? Finish,
    [Required, StringLength(64, MinimumLength = 64)] string ConfirmationToken, [Required] bool? Confirmed);
