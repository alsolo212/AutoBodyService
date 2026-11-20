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

namespace AutoBodyService.IntegrationTests;

[Collection("PostgresIdentity")]
public sealed class WorkshopContactsPostgresTests(PostgresIdentityFixture fixture) : IDisposable
{
    private readonly WebApplicationFactory<Program> factory = fixture.Factory.WithWebHostBuilder(_ => { });
    private const string Path = "/api/admin/workshop-contacts";
    private const string Password = "Example-password-123!";
    public void Dispose() => factory.Dispose();

    private async Task<ApplicationUser> User(string role)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var phone = "+380" + RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000);
        var user = new ApplicationUser { Name = "Test", UserName = phone, PhoneNumber = phone, PhoneNormalized = phone };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return user;
    }
    private HttpClient Client() => factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
    private async Task<HttpClient> Login(ApplicationUser user)
    {
        var client = Client();
        using var csrf = await client.GetAsync("/api/auth/antiforgery");
        var cookie = Assert.Single(csrf.Headers.GetValues("Set-Cookie"), s => s.StartsWith("XSRF-TOKEN="));
        client.DefaultRequestHeaders.Add("RequestVerificationToken", Uri.UnescapeDataString(cookie.Split(';')[0]["XSRF-TOKEN=".Length..]));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/login", new { phone = user.PhoneNumber, password = Password })).StatusCode);
        using var signedInToken = await client.GetAsync("/api/auth/antiforgery");
        var signedInCookie = Assert.Single(signedInToken.Headers.GetValues("Set-Cookie"), s => s.StartsWith("XSRF-TOKEN="));
        client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        client.DefaultRequestHeaders.Add("RequestVerificationToken", Uri.UnescapeDataString(signedInCookie.Split(';')[0]["XSRF-TOKEN=".Length..]));
        return client;
    }

    [PostgresFact]
    public async Task UpdatesRequireCurrentStaffRoleAndCsrfAndPublishOnlyValidatedAuditedChanges()
    {
        var admin = await User("Admin"); var supervisor = await User("SuperAdmin"); var user = await User("User");
        using var staff = await Login(admin); using var super = await Login(supervisor); using var customer = await Login(user); using var anonymous = Client();
        var initial = await staff.GetFromJsonAsync<JsonElement>(Path);
        var version = initial.GetProperty("version").GetInt32();
        var body = new { phone = "(000) 000-00-01", address = " Тестова адреса 1 ", version };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PutAsJsonAsync(Path, body)).StatusCode);
        staff.DefaultRequestHeaders.Remove("RequestVerificationToken");
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PutAsJsonAsync(Path, body)).StatusCode);
        using var token = await staff.GetAsync("/api/auth/antiforgery");
        var cookie = Assert.Single(token.Headers.GetValues("Set-Cookie"), s => s.StartsWith("XSRF-TOKEN="));
        staff.DefaultRequestHeaders.Add("RequestVerificationToken", Uri.UnescapeDataString(cookie.Split(';')[0]["XSRF-TOKEN=".Length..]));
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PutAsJsonAsync(Path, new { phone = "javascript:alert(1)", address = "Address", version })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PutAsJsonAsync(Path, new { phone = "+380000000001", address = " \n ", version })).StatusCode);
        using var saved = await staff.PutAsJsonAsync(Path, body);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var updated = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("+380000000001", updated.GetProperty("phone").GetString());
        Assert.Equal("Тестова адреса 1", updated.GetProperty("address").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await super.PutAsJsonAsync(Path, new { phone = "+380000000002", address = "Other", version })).StatusCode);
        var newVersion = updated.GetProperty("version").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await super.PutAsJsonAsync(Path, new { phone = "+380000000002", address = "Тестова адреса 1", version = newVersion })).StatusCode);
        var publicData = await anonymous.GetFromJsonAsync<JsonElement>("/api/public/contacts");
        Assert.Equal("+380000000002", publicData.GetProperty("phone").GetString());
        Assert.Equal("Тестова адреса 1", publicData.GetProperty("address").GetString());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var events = await db.AdminAuditEvents.Where(e => e.ActorId == admin.Id || e.ActorId == supervisor.Id).ToListAsync();
            Assert.Equal(3, events.Count);
            Assert.Equal(2, events.Count(e => e.Action == "WorkshopPhoneChanged"));
            Assert.Single(events, e => e.Action == "WorkshopAddressChanged" && e.ActorId == admin.Id);
            Assert.All(events, e => Assert.Equal("{}", e.Details));
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await users.RemoveFromRoleAsync((await users.FindByIdAsync(admin.Id))!, "Admin")).Succeeded);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await staff.PutAsJsonAsync(Path, new { phone = "+380000000003", address = "Other", version = newVersion + 1 })).StatusCode);
    }
}
