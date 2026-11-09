using AutoBodyService.Api.Configuration;
using AutoBodyService.Application.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AutoBodyService.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/public/contacts")]
public sealed class PublicContactsController(IOptions<PublicContactsOptions> options) : ControllerBase
{
    [HttpGet]
    public ActionResult<PublicContactsResponse> Get()
    {
        var contacts = options.Value;
        var phone = string.IsNullOrWhiteSpace(contacts.Phone) ? null : PhoneNumberNormalizer.Normalize(contacts.Phone);
        return Ok(new PublicContactsResponse(phone, Clean(contacts.Address), Clean(contacts.WorkingHours)));
    }

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) || trimmed.Length > 200 ? null : trimmed;
    }
}

/// <summary>Public service contacts; unset or invalid values are null.</summary>
public sealed record PublicContactsResponse(string? Phone, string? Address, string? WorkingHours);
