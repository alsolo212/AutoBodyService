using System.Threading.RateLimiting;
using AutoBodyService.Infrastructure.Identity;

namespace AutoBodyService.Api.Security;

public static class ApiSecurity
{
    public static IServiceCollection AddApiSecurity(this IServiceCollection services)
    {
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "RequestVerificationToken";
            options.Cookie.Name = "__Host-AutoBodyService.Antiforgery";
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });
        services.AddAuthorization(options =>
        {
            options.AddPolicy("User", policy => policy.RequireRole(ApplicationRoles.User, ApplicationRoles.Admin, ApplicationRoles.SuperAdmin));
            options.AddPolicy("Admin", policy => policy.RequireRole(ApplicationRoles.Admin, ApplicationRoles.SuperAdmin));
            options.AddPolicy("SuperAdmin", policy => policy.RequireRole(ApplicationRoles.SuperAdmin));
        });
        // The application is deployed same-origin; no cross-origin browser access is granted.
        services.AddCors(options => options.AddDefaultPolicy(policy => { }));
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                context.Request.Path.StartsWithSegments("/api/auth") && !HttpMethods.IsGet(context.Request.Method)
                    ? RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
                        new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
                    : RateLimitPartition.GetNoLimiter("other"));
        });
        return services;
    }
}
