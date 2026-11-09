using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AutoBodyService.IntegrationTests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AUTOBODY_IDENTITY_POSTGRES")))
            Skip = "Run scripts/test-identity-postgres.sh to start a disposable PostgreSQL container.";
    }
}

[CollectionDefinition("PostgresIdentity", DisableParallelization = true)]
public sealed class PostgresIdentityCollection : ICollectionFixture<PostgresIdentityFixture>;

public sealed class PostgresIdentityFixture : IAsyncLifetime
{
    private string? originalConnection;
    private bool configured;
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("AUTOBODY_IDENTITY_POSTGRES");
        if (string.IsNullOrEmpty(connection)) return;
        var container = Environment.GetEnvironmentVariable("AUTOBODY_IDENTITY_CONTAINER") ?? "";
        var settings = new NpgsqlConnectionStringBuilder(connection);
        if (!container.StartsWith("autobody-identity-test-", StringComparison.Ordinal) || settings.Host != "127.0.0.1" ||
            settings.Database != "autobody_identity_test" || settings.Username != "autobody_identity_test")
            throw new InvalidOperationException("Integration tests require the disposable database created by the test script.");
        var label = await Docker("inspect", "--format", "{{index .Config.Labels \"autobody.identity.disposable\"}}", container);
        var port = await Docker("port", container, "5432/tcp");
        if (label.Trim() != "true" || port.Trim() != $"127.0.0.1:{settings.Port}")
            throw new InvalidOperationException("The disposable test container could not be verified.");
        originalConnection = Environment.GetEnvironmentVariable("ConnectionStrings__AutoBodyService");
        Environment.SetEnvironmentVariable("ConnectionStrings__AutoBodyService", connection);
        configured = true;
        Factory = new WebApplicationFactory<Program>();
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.MigrateAsync();
    }
    public async Task DisposeAsync()
    {
        if (Factory is not null) await Factory.DisposeAsync();
        if (configured) Environment.SetEnvironmentVariable("ConnectionStrings__AutoBodyService", originalConnection);
    }
    private static async Task<string> Docker(params string[] args)
    {
        var info = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Docker is required.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("Unable to verify disposable database: " + error);
        return output;
    }
}

[Collection("PostgresIdentity")]
public sealed class PostgresIdentityTests(PostgresIdentityFixture fixture) : IDisposable
{
    private readonly WebApplicationFactory<Program> factory = fixture.Factory is not null
        ? new WebApplicationFactory<Program>() : throw new InvalidOperationException("Database fixture is unavailable.");
    public void Dispose() => factory.Dispose();
    private const string Password = "Example-password-123!";
    private const string NewPassword = "Another-password-123!";
    private HttpClient Client(bool cookies = true) => factory.CreateClient(new()
        { BaseAddress = new Uri("https://localhost"), HandleCookies = cookies, AllowAutoRedirect = false });
    private static string Phone() => "+380" + RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000);
    private static async Task Token(HttpClient client)
    {
        using var response = await client.GetAsync("/api/auth/antiforgery");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), s => s.StartsWith("XSRF-TOKEN="));
        client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        client.DefaultRequestHeaders.Add("RequestVerificationToken", Uri.UnescapeDataString(cookie.Split(';')[0]["XSRF-TOKEN=".Length..]));
    }
    private static async Task<string> Login(HttpClient client, string phone)
    {
        await Token(client);
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { phone, password = Password });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return Assert.Single(response.Headers.GetValues("Set-Cookie"), s => s.StartsWith(".AspNetCore.Identity.Application="));
    }
    private async Task<ApplicationUser> CreateUser(string phone, bool temporary = false)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { Name = "Test", UserName = phone, PhoneNumber = phone, PhoneNormalized = phone, MustChangePassword = temporary };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, ApplicationRoles.User)).Succeeded);
        return user;
    }

    [PostgresFact]
    public async Task RegistrationNormalizesPhoneAssignsOnlyUserAndRejectsElevation()
    {
        var phone = Phone();
        using var client = Client();
        await Token(client);
        using var response = await client.PostAsJsonAsync("/api/auth/register", new { name = "Test", phone = "0" + phone[4..], password = Password, role = "SuperAdmin", roles = new[] { "Admin" } });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByNameAsync(phone);
        Assert.NotNull(user);
        Assert.Equal(phone, user.PhoneNormalized);
        Assert.Equal(phone, user.PhoneNumber);
        Assert.Equal(new[] { ApplicationRoles.User }, await users.GetRolesAsync(user));
        Assert.True(await users.CheckPasswordAsync(user, Password));
        Assert.NotEqual(Password, user.PasswordHash);
    }

    [PostgresFact]
    public async Task ConcurrentRegistrationKeepsPhoneUniqueAndResponsesIndistinguishable()
    {
        var phone = Phone();
        using var a = Client(); using var b = Client();
        await Token(a); await Token(b);
        var requests = await Task.WhenAll(
            a.PostAsJsonAsync("/api/auth/register", new { name = "First", phone, password = Password }),
            b.PostAsJsonAsync("/api/auth/register", new { name = "Second", phone = phone[1..], password = Password }));
        using var first = requests[0]; using var second = requests[1];
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(first.StatusCode, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await db.Users.CountAsync(user => user.PhoneNormalized == phone));
    }

    [PostgresFact]
    public async Task PostgreSqlConstraintsRejectInvalidAndDuplicatePhones()
    {
        var phone = Phone();
        await CreateUser(phone);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var index = await db.Database.SqlQueryRaw<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'IX_AspNetUsers_PhoneNormalized'").SingleAsync();
        Assert.Contains("UNIQUE INDEX", index, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PhoneNormalized", index, StringComparison.Ordinal);
        db.Users.Add(new ApplicationUser { Name = "Test", PhoneNormalized = phone, PhoneNumber = phone, UserName = phone, NormalizedUserName = phone });
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(duplicate.InnerException).SqlState);
        db.ChangeTracker.Clear();
        db.Users.Add(new ApplicationUser { Name = "Test", PhoneNormalized = "invalid", PhoneNumber = "invalid", UserName = "invalid" });
        var invalid = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var error = Assert.IsType<PostgresException>(invalid.InnerException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("CK_Users_PhoneNormalized", error.ConstraintName);
    }

    [PostgresFact]
    public async Task PasswordVerificationAndProtectedPersistentCookie()
    {
        var phone = Phone(); await CreateUser(phone);
        using var client = Client(); await Token(client);
        using var wrong = await client.PostAsJsonAsync("/api/auth/login", new { phone, password = "Incorrect-password-123!" });
        using var unknown = await client.PostAsJsonAsync("/api/auth/login", new { phone = Phone(), password = "Incorrect-password-123!" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        var a = await wrong.Content.ReadFromJsonAsync<JsonElement>(); var b = await unknown.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(a.GetProperty("title").GetString(), b.GetProperty("title").GetString());
        var beforeLogin = DateTimeOffset.UtcNow;
        var cookie = await Login(client, phone);
        var expiresText = Assert.Single(cookie.Split(';'), part => part.TrimStart().StartsWith("expires=", StringComparison.OrdinalIgnoreCase)).Trim()["expires=".Length..];
        var expires = DateTimeOffset.Parse(expiresText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        Assert.InRange(expires - beforeLogin, TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1), TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        using var session = await client.GetAsync("/api/auth/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        var body = await session.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "mustChangePassword", "name", "phone", "roles" }, body.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray());
        Assert.Equal(phone, body.GetProperty("phone").GetString());
    }

    [PostgresFact]
    public async Task AntiforgeryAndAnonymousRestrictionsProtectMutations()
    {
        var phone = Phone(); await CreateUser(phone);
        using var client = Client();
        using var anonymous = await client.GetAsync("/api/profile");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await Token(client);
        using var noCookieLogin = await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = Password, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, noCookieLogin.StatusCode);
        await Login(client, phone);
        client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        using var missing = await client.PatchAsJsonAsync("/api/profile", new { name = "Changed" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        client.DefaultRequestHeaders.Add("RequestVerificationToken", "invalid");
        using var bad = await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = Password, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var profile = await client.GetAsync("/api/profile");
        Assert.Equal("Test", (await profile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());
    }

    [PostgresFact]
    public async Task ProfileOwnershipIgnoresForeignQueryAndRejectsForeignMutation()
    {
        var mine = await CreateUser(Phone()); var theirs = await CreateUser(Phone());
        using var client = Client(); await Login(client, mine.PhoneNormalized); await Token(client);
        using var get = await client.GetAsync("/api/profile?userId=" + theirs.Id);
        var body = await get.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(mine.PhoneNormalized, body.GetProperty("phone").GetString());
        using var otherRoute = await client.GetAsync("/api/profile/" + theirs.Id);
        Assert.Equal(HttpStatusCode.NotFound, otherRoute.StatusCode);
        using var patch = await client.PatchAsJsonAsync("/api/profile", new { name = "Intrusion", userId = theirs.Id, phone = theirs.PhoneNormalized });
        Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.Equal("Test", (await users.FindByIdAsync(theirs.Id))!.Name);
    }

    [PostgresFact]
    public async Task TemporaryPasswordRestrictsProfileUntilSuccessfulChange()
    {
        var user = await CreateUser(Phone(), temporary: true);
        using var client = Client(); var oldCookie = await Login(client, user.PhoneNormalized); await Token(client);
        using var before = await client.GetAsync("/api/profile"); Assert.Equal(HttpStatusCode.Forbidden, before.StatusCode);
        using var bad = await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = "Incorrect-password-123!", newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var still = await client.GetAsync("/api/auth/session");
        Assert.True((await still.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mustChangePassword").GetBoolean());
        using var change = await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = Password, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        using var after = await client.GetAsync("/api/profile"); Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        using var session = await client.GetAsync("/api/auth/session");
        Assert.False((await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mustChangePassword").GetBoolean());
        using var replay = Client(false); replay.DefaultRequestHeaders.Add("Cookie", oldCookie.Split(';')[0]);
        using var rejected = await replay.GetAsync("/api/auth/session");
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
    }

    [PostgresFact]
    public async Task LogoutRevokesOnlyCurrentCookieEvenWhenReplayed()
    {
        var user = await CreateUser(Phone());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.RevokedSessions.Add(new RevokedSession { IdHash = new string('A', 64), UserId = user.Id, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(-1) });
            await db.SaveChangesAsync();
        }
        using var first = Client(); using var second = Client();
        var cookie = await Login(first, user.PhoneNormalized); await Login(second, user.PhoneNormalized); await Token(first);
        using var logout = await first.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var replica = new WebApplicationFactory<Program>();
        using var replay = replica.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        replay.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        using var revoked = await replay.GetAsync("/api/auth/session");
        using var retained = await second.GetAsync("/api/auth/session");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retained.StatusCode);
        await using var verifyScope = factory.Services.CreateAsyncScope();
        var dbAfter = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = Assert.Single(await dbAfter.RevokedSessions.Where(row => row.UserId == user.Id).ToListAsync());
        Assert.Matches("^[A-F0-9]{64}$", row.IdHash);
        Assert.NotEqual(new string('A', 64), row.IdHash);
        Assert.True(row.ExpiresUtc > DateTimeOffset.UtcNow.AddDays(7));
        Assert.True(row.ExpiresUtc < DateTimeOffset.UtcNow.AddDays(15));
    }

    [PostgresFact]
    public async Task ResetPasswordAndBlockedAccountRevokeExistingSessions()
    {
        var user = await CreateUser(Phone());
        using var client = Client(); await Login(client, user.PhoneNormalized);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var current = (await users.FindByIdAsync(user.Id))!;
            var token = await users.GeneratePasswordResetTokenAsync(current);
            Assert.True((await users.ResetPasswordAsync(current, token, NewPassword)).Succeeded);
        }
        using var rejected = await client.GetAsync("/api/auth/session");
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var current = (await users.FindByIdAsync(user.Id))!;
            current.IsBlocked = true;
            Assert.True((await users.UpdateAsync(current)).Succeeded);
        }
        await Token(client);
        using var blocked = await client.PostAsJsonAsync("/api/auth/login", new { phone = user.PhoneNormalized, password = NewPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, blocked.StatusCode);
    }
    [PostgresFact]
    public async Task RoleChangesAndBlockUnblockRevokePreviouslyIssuedCookies()
    {
        var roleUser = await CreateUser(Phone());
        var blockedUser = await CreateUser(Phone());
        using var roleClient = Client(); using var blockedClient = Client();
        await Login(roleClient, roleUser.PhoneNormalized); await Login(blockedClient, blockedUser.PhoneNormalized);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var account = (await users.FindByIdAsync(roleUser.Id))!;
            Assert.True((await users.AddToRoleAsync(account, ApplicationRoles.Admin)).Succeeded);
            Assert.True((await users.RemoveFromRoleAsync(account, ApplicationRoles.Admin)).Succeeded);
            var blocked = (await users.FindByIdAsync(blockedUser.Id))!;
            blocked.IsBlocked = true;
            Assert.True((await users.UpdateAsync(blocked)).Succeeded);
            blocked.IsBlocked = false;
            Assert.True((await users.UpdateAsync(blocked)).Succeeded);
        }
        using var roleResponse = await roleClient.GetAsync("/api/auth/session");
        using var blockedResponse = await blockedClient.GetAsync("/api/auth/session");
        Assert.Equal(HttpStatusCode.Unauthorized, roleResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, blockedResponse.StatusCode);
    }

    [PostgresFact]
    public async Task ConcurrentLogoutsDoNotRestoreTheSameSession()
    {
        var user = await CreateUser(Phone());
        using var client = Client();
        var cookie = await Login(client, user.PhoneNormalized);
        await Token(client);
        var results = await Task.WhenAll(client.PostAsync("/api/auth/logout", null), client.PostAsync("/api/auth/logout", null));
        using var first = results[0]; using var second = results[1];
        Assert.Contains(results, response => response.StatusCode == HttpStatusCode.NoContent);
        Assert.All(results, response => Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NoContent, HttpStatusCode.Unauthorized }));
        using var replay = Client(false);
        replay.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        using var denied = await replay.GetAsync("/api/auth/session");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

}
