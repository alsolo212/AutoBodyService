using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using AutoBodyService.Application.Profiles;
using AutoBodyService.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace AutoBodyService.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/profile")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ProfileController(IProfileService profiles, UserManager<ApplicationUser> users) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var id = users.GetUserId(User);
        if (id is null) return Unauthorized();
        var profile = await profiles.GetAsync(id);
        return profile is null ? Unauthorized() : Ok(new ProfileResponse(profile.Name, profile.Phone));
    }

    [HttpPatch]
    public async Task<IActionResult> Update(UpdateProfileRequest request)
    {
        var name = request.Name.Trim();
        if (name.Length == 0) return BadRequest(new ProblemDetails { Status = 400, Title = "Invalid profile." });
        var id = users.GetUserId(User);
        if (id is null) return Unauthorized();
        return await profiles.UpdateNameAsync(id, name) switch
        {
            ProfileUpdateResult.Success => NoContent(),
            ProfileUpdateResult.NotFound => Unauthorized(),
            _ => Conflict(new ProblemDetails { Status = 409, Title = "Profile has changed. Please try again." })
        };
    }
}

/// <summary>Non-secret details of the current user's profile.</summary>
public sealed record ProfileResponse(string Name, string Phone);

/// <summary>The new name for the current user's profile.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateProfileRequest
{
    [Required, StringLength(200)] public required string Name { get; init; }
}
