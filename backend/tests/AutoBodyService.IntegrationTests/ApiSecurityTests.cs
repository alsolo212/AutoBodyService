using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using AutoBodyService.Infrastructure.Persistence;
using AutoBodyService.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AutoBodyService.IntegrationTests;

public sealed class ApiSecurityTests
{
    private static WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();
            services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase("test-" + services.GetHashCode()));
        }));

    [Fact]
    public async Task AntiforgeryTokenRequiresMatchingCookieAndHeaderForLogin()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        using var missing = await client.PostAsJsonAsync("/api/auth/login", new { phone = "+380000000000", password = "invalid" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var tokenResponse = await client.GetAsync("/api/auth/antiforgery");
        Assert.Equal(HttpStatusCode.NoContent, tokenResponse.StatusCode);
        var cookies = tokenResponse.Headers.GetValues("Set-Cookie").ToArray();
        Assert.Contains(cookies, c => c.StartsWith("__Host-AutoBodyService.Antiforgery=") && c.Contains("secure", StringComparison.OrdinalIgnoreCase) && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        var tokenCookie = Assert.Single(cookies, c => c.StartsWith("XSRF-TOKEN="));
        Assert.Contains("samesite=strict", tokenCookie, StringComparison.OrdinalIgnoreCase);
        var token = Uri.UnescapeDataString(tokenCookie.Split(';')[0]["XSRF-TOKEN=".Length..]);
        client.DefaultRequestHeaders.Add("RequestVerificationToken", token);
        using var accepted = await client.PostAsJsonAsync("/api/auth/login", new { phone = "0000000000000", password = "invalid" });
        Assert.Equal(HttpStatusCode.Unauthorized, accepted.StatusCode);
        client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        client.DefaultRequestHeaders.Add("RequestVerificationToken", "wrong");
        using var wrong = await client.PostAsJsonAsync("/api/auth/login", new { phone = "+380000000000", password = "invalid" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
    }

    [Fact]
    public async Task AnonymousSessionReturns401AndCorsDoesNotTrustArbitraryOrigin()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/session");
        request.Headers.Add("Origin", "https://attacker.example");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task AuthenticationWritesAreRateLimited()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        for (var i = 0; i < 10; i++)
        {
            using var response = await client.PostAsync("/api/auth/login", null);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var limited = await client.PostAsync("/api/auth/login", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }
    [Theory]
    [InlineData("block")]
    [InlineData("role")]
    [InlineData("password")]
    public async Task PersistentLoginIsRevokedAfterSecurityChange(string change)
    {
        using var factory = CreateFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.EnsureCreatedAsync();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { Name = "Test", UserName = "+380000000000", PhoneNumber = "+380000000000", PhoneNormalized = "+380000000000" };
            Assert.True((await users.CreateAsync(user, "Example-password-123!")).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, ApplicationRoles.User)).Succeeded);
        }
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        using var csrf = await client.GetAsync("/api/auth/antiforgery");
        var tokenCookie = Assert.Single(csrf.Headers.GetValues("Set-Cookie"), c => c.StartsWith("XSRF-TOKEN="));
        client.DefaultRequestHeaders.Add("RequestVerificationToken", Uri.UnescapeDataString(tokenCookie.Split(';')[0]["XSRF-TOKEN=".Length..]));
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { phone = "+380000000000", password = "Example-password-123!" });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var authCookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".AspNetCore.Identity.Application="));
        Assert.Contains("expires=", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", authCookie, StringComparison.OrdinalIgnoreCase);
        using var before = await client.GetAsync("/api/auth/session");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByNameAsync("+380000000000"))!;
            if (change == "block")
            {
                user.IsBlocked = true;
                Assert.True((await users.UpdateAsync(user)).Succeeded);
                user.IsBlocked = false;
                Assert.True((await users.UpdateAsync(user)).Succeeded);
            }
            else if (change == "role")
            {
                Assert.True((await users.AddToRoleAsync(user, ApplicationRoles.Admin)).Succeeded);
                Assert.True((await users.RemoveFromRoleAsync(user, ApplicationRoles.Admin)).Succeeded);
            }
            else
            {
                var token = await users.GeneratePasswordResetTokenAsync(user);
                Assert.True((await users.ResetPasswordAsync(user, token, "Another-password-123!")).Succeeded);
            }
        }
        using var after = await client.GetAsync("/api/auth/session");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Theory]
    [InlineData("User", true, false, false)]
    [InlineData("Admin", true, true, false)]
    [InlineData("SuperAdmin", true, true, true)]
    public async Task RolePoliciesEnforceHierarchy(string role, bool userAllowed, bool adminAllowed, bool superAdminAllowed)
    {
        using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));
        Assert.Equal(userAllowed, (await authorization.AuthorizeAsync(principal, null, "User")).Succeeded);
        Assert.Equal(adminAllowed, (await authorization.AuthorizeAsync(principal, null, "Admin")).Succeeded);
        Assert.Equal(superAdminAllowed, (await authorization.AuthorizeAsync(principal, null, "SuperAdmin")).Succeeded);
    }

}
