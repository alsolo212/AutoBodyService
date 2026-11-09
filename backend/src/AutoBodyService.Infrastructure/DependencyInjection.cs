using AutoBodyService.Infrastructure.Persistence;
using AutoBodyService.Application.Authentication;
using AutoBodyService.Application.Profiles;
using AutoBodyService.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AutoBodyService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string? connectionString)
    {
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(!string.IsNullOrWhiteSpace(connectionString)
                ? connectionString
                : throw new InvalidOperationException("Connection string 'AutoBodyService' is not configured.")));

        services.AddIdentity<ApplicationUser, IdentityRole>(options =>
        {
            options.Password.RequiredLength = 12;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.User.AllowedUserNameCharacters = "+0123456789";
        }).AddEntityFrameworkStores<ApplicationDbContext>().AddDefaultTokenProviders();

        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.ExpireTimeSpan = TimeSpan.FromDays(7);
            options.SlidingExpiration = true;
            options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
            options.Events.OnSigningIn = context =>
            {
                var identity = (ClaimsIdentity)context.Principal!.Identity!;
                foreach (var claim in identity.FindAll(SessionRevocations.ClaimType).ToArray()) identity.RemoveClaim(claim);
                identity.AddClaim(new Claim(SessionRevocations.ClaimType, Guid.NewGuid().ToString("N")));
                return Task.CompletedTask;
            };
            options.Events.OnValidatePrincipal = async context =>
            {
                await SecurityStampValidator.ValidatePrincipalAsync(context);
                if (context.Principal?.Identity?.IsAuthenticated == true)
                {
                    var manager = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
                    var user = await manager.GetUserAsync(context.Principal);
                    var roles = user is null ? Array.Empty<string>() : await manager.GetRolesAsync(user);
                    var cookieRoles = context.Principal.FindAll(ClaimTypes.Role).Select(claim => claim.Value);
                    var sessionId = context.Principal.FindFirstValue(SessionRevocations.ClaimType);
                    var revocations = context.HttpContext.RequestServices.GetRequiredService<SessionRevocations>();
                    if (user is null || user.IsBlocked || sessionId is null ||
                        !Guid.TryParseExact(sessionId, "N", out _) ||
                        await revocations.IsRevokedAsync(sessionId, user.Id) ||
                        !roles.ToHashSet(StringComparer.Ordinal).SetEquals(cookieRoles))
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
                    }
                }
            };
        });
        services.Configure<SecurityStampValidatorOptions>(options =>
        {
            options.ValidationInterval = TimeSpan.Zero;
            options.OnRefreshingPrincipal = context =>
            {
                var session = context.CurrentPrincipal?.FindFirst(SessionRevocations.ClaimType);
                if (context.NewPrincipal?.Identity is ClaimsIdentity identity)
                {
                    foreach (var claim in identity.FindAll(SessionRevocations.ClaimType).ToArray()) identity.RemoveClaim(claim);
                    if (session is not null) identity.AddClaim(session);
                }
                return Task.CompletedTask;
            };
        });
        services.AddSingleton<DummyPasswordVerifier>();
        services.AddScoped<SessionRevocations>();
        services.AddScoped<SuperAdminInitializer>();
        services.AddScoped<IRegistrationService, RegistrationService>();
        services.AddScoped<IProfileService, ProfileService>();

        return services;
    }
}
