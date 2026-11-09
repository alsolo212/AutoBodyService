using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Application.Profiles;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AutoBodyService.IntegrationTests;

public sealed class ProfileApiTests
{
    private const string OldPassword = "Example-password-123!";
    private const string NewPassword = "Another-password-123!";

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var name = Guid.NewGuid().ToString();
        var postgres = Environment.GetEnvironmentVariable("PROFILE_TEST_POSTGRES");
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();
            services.AddDbContext<ApplicationDbContext>(options =>
            {
                if (postgres is not null) options.UseNpgsql(postgres);
                else options.UseInMemoryDatabase(name);
            });
        }));
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new()
        { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });

    private static async Task<(string Id, string Phone)> CreateUser(WebApplicationFactory<Program> factory, string name, bool mustChange = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.EnsureCreatedAsync();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var phone = "+380" + RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000);
        var user = new ApplicationUser { Name = name, PhoneNormalized = phone, PhoneNumber = phone, UserName = phone, MustChangePassword = mustChange };
        Assert.True((await users.CreateAsync(user, OldPassword)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, ApplicationRoles.User)).Succeeded);
        return (user.Id, phone);
    }

    private static async Task Token(HttpClient client)
    {
        using var response = await client.GetAsync("/api/auth/antiforgery");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith("XSRF-TOKEN="));
        client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        client.DefaultRequestHeaders.Add("RequestVerificationToken", Uri.UnescapeDataString(cookie.Split(';')[0]["XSRF-TOKEN=".Length..]));
    }

    private static async Task SignIn(HttpClient client, string phone)
    {
        await Token(client);
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { phone, password = OldPassword });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        await Token(client);
    }

    [Fact]
    public async Task OwnProfileRejectsOtherIdentityAndPhoneChanges()
    {
        using var factory = CreateFactory();
        var own = await CreateUser(factory, "First");
        var other = await CreateUser(factory, "Second");
        using var client = Client(factory);
        using var anonymous = await client.GetAsync("/api/profile");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await SignIn(client, own.Phone);
        using var get = await client.GetAsync("/api/profile?userId=" + other.Id);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var json = await get.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "name", "phone" }, json.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToArray());
        Assert.Equal("First", json.GetProperty("name").GetString());
        Assert.Equal(own.Phone, json.GetProperty("phone").GetString());
        using var foreignRoute = await client.GetAsync("/api/profile/" + other.Id);
        Assert.Equal(HttpStatusCode.NotFound, foreignRoute.StatusCode);
        using var forbidden = await client.PatchAsJsonAsync("/api/profile", new { name = "Attack", userId = other.Id, phone = other.Phone });
        Assert.Equal(HttpStatusCode.BadRequest, forbidden.StatusCode);
        using var invalid = await client.PatchAsJsonAsync("/api/profile", new { name = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var tooLong = await client.PatchAsJsonAsync("/api/profile", new { name = new string('x', 201) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        using var noCsrf = await client.PatchAsJsonAsync("/api/profile", new { name = "Changed" });
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        await Token(client);
        using var update = await client.PatchAsJsonAsync("/api/profile", new { name = "  Updated  " });
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        using var after = await client.GetAsync("/api/profile");
        var updated = await after.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Updated", updated.GetProperty("name").GetString());
        Assert.Equal(own.Phone, updated.GetProperty("phone").GetString());
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var unchanged = (await users.FindByIdAsync(other.Id))!;
        Assert.Equal("Second", unchanged.Name);
        Assert.Equal(other.Phone, unchanged.PhoneNormalized);
    }

    [Fact]
    public async Task PasswordChangeRefreshesCurrentCookieAndRevokesOtherSessions()
    {
        using var factory = CreateFactory();
        var user = await CreateUser(factory, "Test");
        using var current = Client(factory);
        using var other = Client(factory);
        await SignIn(current, user.Phone);
        await SignIn(other, user.Phone);
        using var change = await current.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = OldPassword, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        var cookie = Assert.Single(change.Headers.GetValues("Set-Cookie"), value => value.StartsWith(".AspNetCore.Identity.Application="));
        Assert.Contains("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        using var valid = await current.GetAsync("/api/auth/session");
        using var revoked = await other.GetAsync("/api/auth/session");
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var updated = (await users.FindByIdAsync(user.Id))!;
        Assert.False(await users.CheckPasswordAsync(updated, OldPassword));
        Assert.True(await users.CheckPasswordAsync(updated, NewPassword));
    }

    [Fact]
    public async Task TemporaryPasswordRequiresCorrectChangeBeforeProfileAccess()
    {
        using var factory = CreateFactory();
        var user = await CreateUser(factory, "Test", mustChange: true);
        using var client = Client(factory);
        await SignIn(client, user.Phone);
        using var contacts = await client.GetAsync("/api/public/contacts");
        Assert.Equal(HttpStatusCode.OK, contacts.StatusCode);
        using var blockedGet = await client.GetAsync("/api/profile");
        using var blockedPatch = await client.PatchAsJsonAsync("/api/profile", new { name = "Blocked" });
        Assert.Equal(HttpStatusCode.Forbidden, blockedGet.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, blockedPatch.StatusCode);
        using var wrong = await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = "Incorrect-password-123!", newPassword = NewPassword });
        using var weak = await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = OldPassword, newPassword = "weak" });
        using var same = await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = OldPassword, newPassword = OldPassword });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, same.StatusCode);
        using var before = await client.GetAsync("/api/auth/session");
        Assert.True((await before.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mustChangePassword").GetBoolean());
        using var success = await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = OldPassword, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.NoContent, success.StatusCode);
        using var after = await client.GetAsync("/api/auth/session");
        Assert.False((await after.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mustChangePassword").GetBoolean());
        using var profile = await client.GetAsync("/api/profile");
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
    }

    [Fact]
    public async Task PasswordChangeRequiresAuthenticationAndAntiforgery()
    {
        using var factory = CreateFactory();
        var user = await CreateUser(factory, "Test");
        using var client = Client(factory);
        using var anonymous = await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = OldPassword, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await SignIn(client, user.Phone);
        client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        using var noToken = await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = OldPassword, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        await Token(client);
        using var missing = await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = OldPassword });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var tooLong = await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = OldPassword, newPassword = new string('x', 257) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }
    [Fact]
    public async Task ConcurrentAccountUpdateDoesNotClearMandatoryFlagOrChangePassword()
    {
        using var factory = CreateFactory();
        var user = await CreateUser(factory, "Test", mustChange: true);
        using var staleScope = factory.Services.CreateScope();
        var staleUsers = staleScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.NotNull(await staleUsers.FindByIdAsync(user.Id));
        using (var currentScope = factory.Services.CreateScope())
        {
            var users = currentScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var account = (await users.FindByIdAsync(user.Id))!;
            account.Name = "Updated concurrently";
            Assert.True((await users.UpdateAsync(account)).Succeeded);
        }
        var profiles = staleScope.ServiceProvider.GetRequiredService<IProfileService>();
        Assert.Equal(PasswordChangeResult.Conflict, await profiles.ChangePasswordAsync(user.Id, OldPassword, NewPassword));
        using var verifyScope = factory.Services.CreateScope();
        var verifyUsers = verifyScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var persisted = (await verifyUsers.FindByIdAsync(user.Id))!;
        Assert.Equal("Updated concurrently", persisted.Name);
        Assert.True(persisted.MustChangePassword);
        Assert.True(await verifyUsers.CheckPasswordAsync(persisted, OldPassword));
        Assert.False(await verifyUsers.CheckPasswordAsync(persisted, NewPassword));
    }

}
