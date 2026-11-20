using AutoBodyService.Api.Configuration;
using AutoBodyService.Application.Authentication;
using AutoBodyService.Application.Contacts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AutoBodyService.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/public/contacts")]
public sealed class PublicContactsController(IOptions<PublicContactsOptions> options, IWorkshopContacts store) : ControllerBase
{
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<PublicContactsResponse>> Get(CancellationToken cancellationToken)
    {
        var contacts = options.Value;
        var saved = await store.GetAsync(cancellationToken);
        var phone = saved.Phone ?? (string.IsNullOrWhiteSpace(contacts.Phone) ? null : PhoneNumberNormalizer.Normalize(contacts.Phone));
        return Ok(new PublicContactsResponse(phone, saved.Address ?? Clean(contacts.Address), Clean(contacts.WorkingHours)));
    }

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) || trimmed.Length > 200 ? null : trimmed;
    }
}

/// <summary>Public service contacts; unset or invalid values are null.</summary>
public sealed record PublicContactsResponse(string? Phone, string? Address, string? WorkingHours);
