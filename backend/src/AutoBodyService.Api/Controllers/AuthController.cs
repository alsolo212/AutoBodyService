using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using AutoBodyService.Infrastructure.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace AutoBodyService.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users, IAntiforgery antiforgery) : ControllerBase
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

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        if (!Regex.IsMatch(request.Phone, @"\A\+380[0-9]{9}\z"))
            return Unauthorized();
        var user = await users.FindByNameAsync(request.Phone);
        if (user is null || user.IsBlocked)
            return Unauthorized();
        var result = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (!result.Succeeded)
            return Unauthorized();
        await signIn.SignInAsync(user, isPersistent: true);
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
    [Required, StringLength(13, MinimumLength = 13)] public required string Phone { get; init; }
    [Required, StringLength(256)] public required string Password { get; init; }
}

/// <summary>Non-secret details of the current account.</summary>
public sealed record SessionResponse(string Name, string Phone, bool MustChangePassword, IEnumerable<string> Roles);
