using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json.Serialization;
using AutoBodyService.Api.Configuration;
using AutoBodyService.Application.Authentication;
using AutoBodyService.Application.Contacts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AutoBodyService.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/admin/workshop-contacts")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminWorkshopContactsController(IWorkshopContacts store, IOptions<PublicContactsOptions> options) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<WorkshopContacts>> Get(CancellationToken cancellationToken)
    {
        var saved = await store.GetAsync(cancellationToken);
        var configured = options.Value;
        var phone = string.IsNullOrWhiteSpace(configured.Phone) ? null : PhoneNumberNormalizer.Normalize(configured.Phone);
        var address = configured.Address?.Trim();
        return Ok(saved with { Phone = saved.Phone ?? phone, Address = saved.Address ?? (address?.Length is > 0 and <= 200 ? address : null) });
    }

    [HttpPut]
    public async Task<ActionResult<WorkshopContacts>> Update(UpdateWorkshopContactsRequest request, CancellationToken cancellationToken)
    {
        var phone = PhoneNumberNormalizer.Normalize(request.Phone ?? "");
        var address = request.Address?.Trim();
        if (phone is null || string.IsNullOrWhiteSpace(address) || address.Length > 200 || address.Any(char.IsControl) || request.Version is null or < 0)
            return BadRequest(new ProblemDetails { Status = 400, Title = "Перевірте телефон та адресу майстерні." });
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actor is null) return Unauthorized();
        var result = await store.UpdateAsync(phone, address, request.Version.Value, actor, cancellationToken);
        return result is null ? Conflict(new ProblemDetails { Status = 409, Title = "Контакти змінилися. Оновіть дані перед збереженням." }) : Ok(result);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateWorkshopContactsRequest
{
    [Required] public string? Phone { get; init; }
    [Required] public string? Address { get; init; }
    [Required] public int? Version { get; init; }
}
