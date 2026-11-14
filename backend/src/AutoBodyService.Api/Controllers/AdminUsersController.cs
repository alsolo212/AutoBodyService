using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json.Serialization;
using AutoBodyService.Application.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AutoBodyService.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/admin/users")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminUsersController(IAdminUsers accounts, IOptions<IdentityOptions> identity) : ControllerBase
{
    private AccountActor? Actor() => User.FindFirstValue(ClaimTypes.NameIdentifier) is { } id &&
        User.FindFirstValue(identity.Value.ClaimsIdentity.SecurityStampClaimType) is { } stamp ? new(id, stamp) : null;

    /// <summary>Searches accounts the current administrator is permitted to manage.</summary>
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery, StringLength(100)] string? search, CancellationToken ct)
    {
        if (Actor() is not { } actor) return Unauthorized();
        var (result, rows) = await accounts.SearchAsync(actor, search, ct);
        return result == AccountOperationResult.Success ? Ok(rows) : Forbid();
    }

    /// <summary>Resets an allowed account and returns its temporary password once.</summary>
    [HttpPost("{id}/reset-password")]
    public async Task<IActionResult> ResetPassword(string id, AccountVersionRequest request, CancellationToken ct)
    {
        if (Actor() is not { } actor) return Unauthorized();
        var result = await accounts.ResetPasswordAsync(actor, id, request.Version!, ct);
        return result.Result == AccountOperationResult.Success ? Ok(new TemporaryPasswordResponse(result.TemporaryPassword!)) : Failure(result.Result);
    }

    /// <summary>Grants or removes the Admin role from an allowed account.</summary>
    [Authorize(Policy = "SuperAdmin")]
    [HttpPut("{id}/roles")]
    public async Task<IActionResult> SetRoles(string id, AccountAdminRequest request, CancellationToken ct)
    {
        if (Actor() is not { } actor) return Unauthorized();
        return Failure((await accounts.SetAdminAsync(actor, id, request.Version!, request.IsAdmin!.Value, ct)).Result);
    }

    /// <summary>Blocks or unblocks an allowed account.</summary>
    [Authorize(Policy = "SuperAdmin")]
    [HttpPatch("{id}/block")]
    public async Task<IActionResult> SetBlocked(string id, AccountBlockRequest request, CancellationToken ct)
    {
        if (Actor() is not { } actor) return Unauthorized();
        return Failure((await accounts.SetBlockedAsync(actor, id, request.Version!, request.IsBlocked!.Value, ct)).Result);
    }

    private IActionResult Failure(AccountOperationResult result) => result switch
    {
        AccountOperationResult.Success => NoContent(),
        AccountOperationResult.Forbidden => Forbid(),
        AccountOperationResult.NotFound => NotFound(),
        _ => Conflict(new ProblemDetails { Status = 409, Title = "Обліковий запис змінився. Оновіть список." })
    };
}

/// <summary>Current version of the account to reset.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountVersionRequest
{
    [Required, StringLength(128, MinimumLength = 1)] public string? Version { get; init; }
}

/// <summary>Requested Admin membership and current account version.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountAdminRequest
{
    [Required, StringLength(128, MinimumLength = 1)] public string? Version { get; init; }
    [Required] public bool? IsAdmin { get; init; }
}

/// <summary>Requested blocked state and current account version.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountBlockRequest
{
    [Required, StringLength(128, MinimumLength = 1)] public string? Version { get; init; }
    [Required] public bool? IsBlocked { get; init; }
}

/// <summary>A temporary password shown only in this response.</summary>
public sealed record TemporaryPasswordResponse(string TemporaryPassword);
