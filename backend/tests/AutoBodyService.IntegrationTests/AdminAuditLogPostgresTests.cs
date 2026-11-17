using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Infrastructure.Audit;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AutoBodyService.IntegrationTests;

[Collection("PostgresIdentity")]
public sealed class AdminAuditLogPostgresTests(PostgresIdentityFixture fixture) : IDisposable
{
    private readonly WebApplicationFactory<Program> factory = fixture.Factory.WithWebHostBuilder(_ => { });
    private const string Password = "Example-password-123!";
    private const string Path = "/api/admin/audit-log";
    public void Dispose() => factory.Dispose();

    private async Task<ApplicationUser> User(string role)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var phone = "+380" + RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000);
        var user = new ApplicationUser { Name = "Test", UserName = phone, PhoneNumber = phone, PhoneNormalized = phone };
        Assert.True((await manager.CreateAsync(user, Password)).Succeeded);
        Assert.True((await manager.AddToRoleAsync(user, role)).Succeeded);
        return user;
    }

    private HttpClient Client() => factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
    private async Task<HttpClient> Login(ApplicationUser user)
    {
        var client = Client();
        using var token = await client.GetAsync("/api/auth/antiforgery");
        var cookie = Assert.Single(token.Headers.GetValues("Set-Cookie"), s => s.StartsWith("XSRF-TOKEN="));
        client.DefaultRequestHeaders.Add("RequestVerificationToken", Uri.UnescapeDataString(cookie.Split(';')[0]["XSRF-TOKEN=".Length..]));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/login", new { phone = user.PhoneNumber, password = Password })).StatusCode);
        return client;
    }

    [PostgresFact]
    public async Task HistoryRequiresSuperAdminAndRejectsInvalidQueriesAndMutationMethods()
    {
        var supervisor = await User("SuperAdmin"); var admin = await User("Admin"); var user = await User("User");
        using var super = await Login(supervisor); using var regularAdmin = await Login(admin); using var customer = await Login(user); using var anonymous = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await regularAdmin.GetAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await super.GetAsync(Path)).StatusCode);
        foreach (var query in new[] { "?page=0", "?pageSize=101", "?direction=sideways", "?action=%20", "?fromUtc=2025-01-02T00%3A00%3A00Z&toUtc=2025-01-01T00%3A00%3A00Z", "?fromUtc=2025-01-01T00%3A00%3A00%2B02%3A00" })
            Assert.Equal(HttpStatusCode.BadRequest, (await super.GetAsync(Path + query)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await super.DeleteAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await super.PatchAsync(Path, null)).StatusCode);
    }

    [PostgresFact]
    public async Task ExpenseHistoryExposesExactDecimalWithoutJavaScriptRounding()
    {
        var supervisor = await User("SuperAdmin"); using var client = await Login(supervisor);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var writer = new AdminAuditWriter(db, TimeProvider.System);
            await using var transaction = await db.Database.BeginTransactionAsync();
            writer.RecordExpenseChange(supervisor.Id, Guid.NewGuid(), null, 9007199254740993.125m, false, true, true);
            await db.SaveChangesAsync(); await transaction.CommitAsync();
        }
        var page = await client.GetFromJsonAsync<JsonElement>($"{Path}?actorId={supervisor.Id}&action=ExpenseCreated");
        var entry = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("9007199254740993.125", entry.GetProperty("details").GetProperty("after").GetProperty("amountExact").GetString());
    }

    [PostgresFact]
    public async Task HistoryFiltersUtcActionActorAndOrdersStablePagesWithoutExposingUnknownFields()
    {
        var supervisor = await User("SuperAdmin"); var actor = await User("Admin"); var other = await User("Admin");
        using var super = await Login(supervisor);
        var firstTime = new DateTimeOffset(2040, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var fake = new FakeTimeProvider(firstTime);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var writer = new AdminAuditWriter(db, fake);
            await using var tx = await db.Database.BeginTransactionAsync();
            writer.RecordRepairPlanChange(actor.Id, Guid.NewGuid(), true, false);
            fake.Advance(TimeSpan.FromSeconds(1));
            writer.RecordAccountAction(actor.Id, Guid.NewGuid().ToString(), "AccountPasswordReset", null);
            writer.RecordRepairPlanChange(actor.Id, Guid.NewGuid(), false, true);
            writer.RecordRepairPlanChange(other.Id, Guid.NewGuid(), true, true);
            await db.SaveChangesAsync(); await tx.CommitAsync();
            // A historical row may have extra JSON fields; the read contract must never echo them.
            var details = "{\"priceChanged\":true,\"datesChanged\":false,\"password\":\"unsafe-marker\",\"reason\":\"unsafe-marker\"}";
            var id = Guid.NewGuid(); var objectId = Guid.NewGuid().ToString(); var when = firstTime.AddSeconds(2);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"AdminAuditEvents\" (\"Id\", \"ActorId\", \"Action\", \"ObjectType\", \"ObjectId\", \"OccurredAtUtc\", \"Details\") VALUES ({id}, {actor.Id}, {"RepairPlanChanged"}, {"Appointment"}, {objectId}, {when}, CAST({details} AS jsonb))");
        }
        var query = $"?actorId={actor.Id}&fromUtc=2040-01-01T12%3A00%3A01Z&toUtc=2040-01-01T12%3A00%3A03Z&direction=asc&pageSize=1";
        var pages = new List<JsonElement>();
        for (var page = 1; page <= 3; page++)
        {
            using var response = await super.GetAsync(Path + query + $"&page={page}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(3, body.GetProperty("total").GetInt32());
            pages.Add(Assert.Single(body.GetProperty("items").EnumerateArray()));
            Assert.DoesNotContain("unsafe-marker", body.ToString());
        }
        Assert.Equal(3, pages.Select(e => e.GetProperty("id").GetGuid()).Distinct().Count());
        Assert.True(pages[0].GetProperty("occurredAtUtc").GetDateTimeOffset() <= pages[1].GetProperty("occurredAtUtc").GetDateTimeOffset());
        Assert.True(pages[1].GetProperty("occurredAtUtc").GetDateTimeOffset() < pages[2].GetProperty("occurredAtUtc").GetDateTimeOffset());
        Assert.All(pages, e => Assert.Equal(actor.Id, e.GetProperty("actorId").GetString()));
        Assert.All(pages, e => Assert.Equal(actor.Name, e.GetProperty("actorName").GetString()));
        var filtered = await super.GetFromJsonAsync<JsonElement>(Path + $"?actorId={actor.Id}&action=RepairPlanChanged&direction=desc&pageSize=100");
        Assert.Equal(3, filtered.GetProperty("total").GetInt32());
        var items = filtered.GetProperty("items").EnumerateArray().ToArray();
        Assert.All(items, e => Assert.Equal("RepairPlanChanged", e.GetProperty("action").GetString()));
        Assert.Equal(firstTime.AddSeconds(2), items[0].GetProperty("occurredAtUtc").GetDateTimeOffset());
        Assert.Equal(new[] { "datesChanged", "priceChanged" }, items[0].GetProperty("details").EnumerateObject().Select(x => x.Name).Order().ToArray());
        var none = await super.GetFromJsonAsync<JsonElement>(Path + $"?actorId={actor.Id}&toUtc=2040-01-01T12%3A00%3A00Z");
        Assert.Equal(0, none.GetProperty("total").GetInt32());
        Assert.Empty(none.GetProperty("items").EnumerateArray());
    }
}
