using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AutoBodyService.IntegrationTests;

public sealed class InspectionExceptionsTests
{
    private const string Url = "/api/admin/inspection-exceptions";
    private const string Password = "Example-password-123!";

    [Fact]
    public async Task DateRulesApplyToWeeklyTemplateAndEnforceSecurityAndConcurrency()
    {
        var database = Guid.NewGuid().ToString();
        var postgres = Environment.GetEnvironmentVariable("EXCEPTION_TEST_POSTGRES");
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();
            services.AddDbContext<ApplicationDbContext>(options => { if (postgres is null) options.UseInMemoryDatabase(database); else options.UseNpgsql(postgres); });
        }));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            if (postgres is null) await db.Database.EnsureCreatedAsync();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            foreach (var (role, phone) in new[] { ("User", "+380000000011"), ("Admin", "+380000000012"), ("SuperAdmin", "+380000000013") })
            {
                var user = new ApplicationUser { Name = role, UserName = phone, PhoneNumber = phone, PhoneNormalized = phone };
                Assert.True((await users.CreateAsync(user, Password)).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
            }
        }
        using var anon = Client(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(Url + "?date=2026-11-09")).StatusCode);
        using var userClient = await Login(factory, "+380000000011");
        Assert.Equal(HttpStatusCode.Forbidden, (await userClient.GetAsync(Url + "?date=2026-11-09")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await userClient.PutAsJsonAsync(Url, new { date = "2026-11-09", version = Guid.NewGuid(), exception = (object?)null })).StatusCode);
        using var admin = await Login(factory, "+380000000012");
        using var super = await Login(factory, "+380000000013");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(Url)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(Url + "?date=2026-02-30")).StatusCode);
        var initial = await Read(admin, "2026-11-09");
        Assert.Equal("Europe/Kyiv", initial.GetProperty("timeZone").GetString());
        Assert.False(initial.GetProperty("bookingConflictsChecked").GetBoolean());
        var version = initial.GetProperty("version").GetGuid();
        var weekly = new { version, intervals = new[] { new { dayOfWeek = "Monday", start = "09:00", end = "11:00" }, new { dayOfWeek = "Sunday", start = "02:30", end = "04:30" } } };
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/admin/inspection-schedule", weekly)).StatusCode);
        var monday = await Read(super, "2026-11-09");
        Assert.Equal(4, monday.GetProperty("templateSlots").GetArrayLength());
        Assert.Equal("2026-11-09T07:00:00+00:00", monday.GetProperty("templateSlots")[0].GetProperty("startUtc").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Put(admin, version, "2026-11-09", new { mode = "Closed", intervals = Array.Empty<object>(), slots = Array.Empty<object>() })).StatusCode);
        version = monday.GetProperty("version").GetGuid();
        admin.DefaultRequestHeaders.Remove("RequestVerificationToken");
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(admin, version, "2026-11-09", new { mode = "Closed", intervals = Array.Empty<object>(), slots = Array.Empty<object>() })).StatusCode);
        await Token(admin);
        using (var closed = await Put(admin, version, "2026-11-09", new { mode = "Closed", reason = "Вихідний", intervals = Array.Empty<object>(), slots = Array.Empty<object>() }))
        {
            Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
            Assert.False((await closed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("bookingConflictsChecked").GetBoolean());
        }
        var closedState = await Read(admin, "2026-11-09");
        Assert.Empty(closedState.GetProperty("templateSlots").EnumerateArray());
        Assert.Equal(4, (await Read(admin, "2026-11-16")).GetProperty("templateSlots").GetArrayLength());
        version = closedState.GetProperty("version").GetGuid();
        var replace = new { mode = "Replace", reason = " test ", intervals = new[] { new { start = "10:00", end = "12:00" } }, slots = new[] { new { start = "10:30", mode = "Closed" }, new { start = "13:00", mode = "Open" } } };
        Assert.Equal(HttpStatusCode.OK, (await Put(super, version, "2026-11-09", replace)).StatusCode);
        var changed = await Read(admin, "2026-11-09");
        Assert.Equal("test", changed.GetProperty("exception").GetProperty("reason").GetString());
        Assert.Equal(new[] { "10:00:00", "11:00:00", "11:30:00", "13:00:00" }, changed.GetProperty("templateSlots").EnumerateArray().Select(s => s.GetProperty("start").GetString()).ToArray());
        version = changed.GetProperty("version").GetGuid();
        foreach (var invalid in new object[] {
            new { mode = "Replace", intervals = new[] { new { start = "10:00", end = "10:15" } }, slots = Array.Empty<object>() },
            new { mode = "Replace", intervals = new[] { new { start = "10:00", end = "11:00" }, new { start = "10:30", end = "11:30" } }, slots = Array.Empty<object>() },
            new { mode = "Weekly", intervals = Array.Empty<object>(), slots = new[] { new { start = "09:00", mode = "Open" }, new { start = "09:15", mode = "Closed" } } },
            new { mode = "Closed", intervals = Array.Empty<object>(), slots = new[] { new { start = "09:00", mode = "Open" } } },
            new { mode = "Replace", intervals = Array.Empty<object>(), slots = Array.Empty<object>() },
            new { mode = 7, intervals = Array.Empty<object>(), slots = Array.Empty<object>() },
            new { mode = "Weekly", intervals = Array.Empty<object>(), slots = new[] { new { start = "23:30", mode = "Open" } } }
        }) Assert.Equal(HttpStatusCode.BadRequest, (await Put(admin, version, "2026-11-09", invalid)).StatusCode);
        Assert.Equal(version, (await Read(admin, "2026-11-09")).GetProperty("version").GetGuid());
        var weeklyOverride = new { mode = "Weekly", intervals = Array.Empty<object>(), slots = new[] { new { start = "09:30", mode = "Closed" }, new { start = "12:00", mode = "Open" } } };
        Assert.Equal(HttpStatusCode.OK, (await Put(admin, version, "2026-11-09", weeklyOverride)).StatusCode);
        // Updating the same slot must not violate its unique database index.
        var beforeSlotChange = await Read(admin, "2026-11-09");
        var slotChange = new { mode = "Weekly", intervals = Array.Empty<object>(), slots = new[] { new { start = "09:30", mode = "Open" } } };
        Assert.Equal(HttpStatusCode.OK, (await Put(admin, beforeSlotChange.GetProperty("version").GetGuid(), "2026-11-09", slotChange)).StatusCode);
        var beforeRestore = await Read(admin, "2026-11-09");
        Assert.Equal(HttpStatusCode.OK, (await Put(admin, beforeRestore.GetProperty("version").GetGuid(), "2026-11-09", weeklyOverride)).StatusCode);
        var partial = await Read(admin, "2026-11-09");
        Assert.Equal(new[] { "09:00:00", "10:00:00", "10:30:00", "12:00:00" }, partial.GetProperty("templateSlots").EnumerateArray().Select(s => s.GetProperty("start").GetString()).ToArray());
        Assert.Equal(HttpStatusCode.OK, (await Put(admin, partial.GetProperty("version").GetGuid(), "2026-11-09", null)).StatusCode);
        Assert.Equal(4, (await Read(admin, "2026-11-09")).GetProperty("templateSlots").GetArrayLength());
        var spring = await Read(admin, "2026-03-29");
        Assert.Equal(new[] { "04:00:00" }, spring.GetProperty("templateSlots").EnumerateArray().Select(s => s.GetProperty("start").GetString()).ToArray());
        var autumn = await Read(admin, "2026-10-25");
        Assert.Equal(2, autumn.GetProperty("templateSlots").EnumerateArray().Count(s => s.GetProperty("start").GetString() == "03:00:00"));
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
    private static async Task<HttpClient> Login(WebApplicationFactory<Program> factory, string phone)
    {
        var client = Client(factory); await Token(client);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/login", new { phone, password = Password })).StatusCode);
        await Token(client); return client;
    }
    private static async Task Token(HttpClient client)
    {
        using var response = await client.GetAsync("/api/auth/antiforgery");
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith("XSRF-TOKEN="));
        client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        client.DefaultRequestHeaders.Add("RequestVerificationToken", Uri.UnescapeDataString(cookie.Split(';')[0]["XSRF-TOKEN=".Length..]));
    }
    private static async Task<JsonElement> Read(HttpClient client, string date)
    {
        using var response = await client.GetAsync(Url + "?date=" + date);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    private static Task<HttpResponseMessage> Put(HttpClient client, Guid version, string date, object? exception) => client.PutAsJsonAsync(Url, new { version, date, exception });
}
