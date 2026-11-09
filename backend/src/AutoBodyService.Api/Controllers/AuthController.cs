using System.ComponentModel.DataAnnotations;
using AutoBodyService.Application.Authentication;
using AutoBodyService.Application.Profiles;
using AutoBodyService.Infrastructure.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace AutoBodyService.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users, IAntiforgery antiforgery, IRegistrationService registration, IProfileService profiles) : ControllerBase
{
    [HttpGet("antiforgery")]
    [AllowAnonymous]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult AntiforgeryToken()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        Response.Cookies.Append("XSRF-TOKEN", tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true
        });
        return NoContent();
    }

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        if (User.Identity?.IsAuthenticated == true) return Forbid();
        var name = request.Name.Trim();
        var phone = PhoneNumberNormalizer.Normalize(request.Phone);
        if (name.Length == 0 || phone is null)
            return BadRequest(new ProblemDetails { Status = 400, Title = "Registration could not be completed." });
        if (!await registration.RegisterAsync(name, phone, request.Password, cancellationToken))
            return BadRequest(new ProblemDetails { Status = 400, Title = "Registration could not be completed." });
        return Accepted();
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var phone = PhoneNumberNormalizer.Normalize(request.Phone);
        if (phone is null) return Unauthorized();
        var user = await users.FindByNameAsync(phone);
        if (user is null || user.IsBlocked)
            return Unauthorized();
        var result = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (!result.Succeeded)
            return Unauthorized();
        await signIn.SignInAsync(user, isPersistent: true);
        return NoContent();
    }

    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request)
    {
        var id = users.GetUserId(User);
        if (id is null) return Unauthorized();
        var result = await profiles.ChangePasswordAsync(id, request.CurrentPassword, request.NewPassword);
        if (result == PasswordChangeResult.NotFound) return Unauthorized();
        if (result == PasswordChangeResult.Conflict)
            return Conflict(new ProblemDetails { Status = 409, Title = "Account has changed. Please sign in again." });
        if (result != PasswordChangeResult.Success)
            return BadRequest(new ProblemDetails { Status = 400, Title = "Password could not be changed." });
        var user = await users.FindByIdAsync(id);
        if (user is null || user.IsBlocked) return Unauthorized();
        await signIn.RefreshSignInAsync(user);
        return NoContent();
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        await signIn.SignOutAsync();
        return NoContent();
    }

    [HttpGet("session")]
    [Authorize]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Session()
    {
        var user = await users.GetUserAsync(User);
        if (user is null || user.IsBlocked) return Unauthorized();
        return Ok(new SessionResponse(user.Name, user.PhoneNormalized, user.MustChangePassword, await users.GetRolesAsync(user)));
    }
}

/// <summary>Credentials for an existing account.</summary>
public sealed record LoginRequest
{
    [Required, StringLength(32)] public required string Phone { get; init; }
    [Required, StringLength(256)] public required string Password { get; init; }
}

/// <summary>Non-secret details of the current account.</summary>
public sealed record SessionResponse(string Name, string Phone, bool MustChangePassword, IEnumerable<string> Roles);

/// <summary>Details for a new customer account.</summary>
public sealed record RegisterRequest
{
    [Required, StringLength(200)] public required string Name { get; init; }
    [Required, StringLength(32)] public required string Phone { get; init; }
    [Required, StringLength(256)] public required string Password { get; init; }
}

/// <summary>Current and new passwords for the signed-in account.</summary>
public sealed record ChangePasswordRequest
{
    [Required, StringLength(256)] public required string CurrentPassword { get; init; }
    [Required, StringLength(256)] public required string NewPassword { get; init; }
}
