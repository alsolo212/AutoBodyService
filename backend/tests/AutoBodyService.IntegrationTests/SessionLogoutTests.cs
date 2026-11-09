using System.Net;
using System.Net.Http.Json;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AutoBodyService.IntegrationTests;

public sealed class SessionLogoutTests
{
    [Fact]
    public async Task LogoutRevokesReplayedCookieWithoutEndingAnotherSession()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();
            services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase("logout-" + services.GetHashCode()));
        }));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.EnsureCreatedAsync();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { Name = "Test", UserName = "+380000000000", PhoneNumber = "+380000000000", PhoneNormalized = "+380000000000" };
            Assert.True((await users.CreateAsync(user, "Example-password-123!")).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, ApplicationRoles.User)).Succeeded);
        }
        using var first = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        using var second = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        var oldCookie = await SignIn(first);
        await SignIn(second);
        await Token(first);
        using var logout = await first.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var replay = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        replay.DefaultRequestHeaders.Add("Cookie", oldCookie);
        using var replayResponse = await replay.GetAsync("/api/auth/session");
        using var otherResponse = await second.GetAsync("/api/auth/session");
        Assert.Equal(HttpStatusCode.Unauthorized, replayResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherResponse.StatusCode);
    }

    [Fact]
    public async Task PasswordChangeInvalidatesThePreviousCookieWhileKeepingTheCurrentSession()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();
            services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase("rotate-" + services.GetHashCode()));
        }));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.EnsureCreatedAsync();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { Name = "Test", UserName = "+380000000000", PhoneNumber = "+380000000000", PhoneNormalized = "+380000000000" };
            Assert.True((await users.CreateAsync(user, "Example-password-123!")).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, ApplicationRoles.User)).Succeeded);
        }
        using var current = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        var oldCookie = await SignIn(current);
        await Token(current);
        using var change = await current.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = "Example-password-123!", newPassword = "Another-password-123!" });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        using var replay = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        replay.DefaultRequestHeaders.Add("Cookie", oldCookie);
        using var replayResponse = await replay.GetAsync("/api/auth/session");
        using var currentResponse = await current.GetAsync("/api/auth/session");
        Assert.Equal(HttpStatusCode.Unauthorized, replayResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, currentResponse.StatusCode);
    }

    private static async Task<string> SignIn(HttpClient client)
    {
        await Token(client);
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { phone = "+380000000000", password = "Example-password-123!" });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return Assert.Single(login.Headers.GetValues("Set-Cookie"), value => value.StartsWith(".AspNetCore.Identity.Application=")).Split(';')[0];
    }
    private static async Task Token(HttpClient client)
    {
        using var response = await client.GetAsync("/api/auth/antiforgery");
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith("XSRF-TOKEN="));
        client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        client.DefaultRequestHeaders.Add("RequestVerificationToken", Uri.UnescapeDataString(cookie.Split(';')[0]["XSRF-TOKEN=".Length..]));
    }
}
