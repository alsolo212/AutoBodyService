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

public sealed class InspectionScheduleTests
{
    private const string Url = "/api/admin/inspection-schedule";
    private const string Password = "Example-password-123!";

    [Fact]
    public async Task ScheduleEnforcesRolesValidationCsrfAndVersionedReplacement()
    {
        var database = Guid.NewGuid().ToString();
        var postgres = Environment.GetEnvironmentVariable("INSPECTION_TEST_POSTGRES");
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            if (postgres is null)
            {
                services.RemoveAll<AutoBodyService.Application.InspectionSchedule.IInspectionCalendarService>();
                services.AddScoped<AutoBodyService.Application.InspectionSchedule.IInspectionCalendarService, InMemoryScheduleTestAdapter>();
            }
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();
            services.AddDbContext<ApplicationDbContext>(options =>
            {
                if (postgres is null) options.UseInMemoryDatabase(database);
                else options.UseNpgsql(postgres);
            });
        }));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            if (postgres is null) await db.Database.EnsureCreatedAsync();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            foreach (var (role, phone) in new[] { ("User", "+380000000001"), ("Admin", "+380000000002"), ("SuperAdmin", "+380000000003") })
            {
                var user = new ApplicationUser { Name = role, UserName = phone, PhoneNumber = phone, PhoneNormalized = phone };
                Assert.True((await users.CreateAsync(user, Password)).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
            }
        }
        using var anonymous = Client(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Url)).StatusCode);
        using var userClient = await Login(factory, "+380000000001");
        Assert.Equal(HttpStatusCode.Forbidden, (await userClient.GetAsync(Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await userClient.PutAsJsonAsync(Url, new { version = Guid.NewGuid(), intervals = Array.Empty<object>() })).StatusCode);
        using var admin = await Login(factory, "+380000000002");
        using var superAdmin = await Login(factory, "+380000000003");
        var initial = await Read(admin);
        Assert.Equal("Europe/Kyiv", initial.GetProperty("timeZone").GetString());
        Assert.Empty(initial.GetProperty("intervals").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await superAdmin.GetAsync(Url)).StatusCode);
        var version = initial.GetProperty("version").GetGuid();
        var valid = new { version, intervals = new[] { new { dayOfWeek = "Monday", start = "09:00", end = "12:00" }, new { dayOfWeek = "Monday", start = "12:00", end = "13:30" } } };
        admin.DefaultRequestHeaders.Remove("RequestVerificationToken");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(Url, valid)).StatusCode);
        await Token(admin);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync(Url, valid)).StatusCode);
        var updated = await Read(superAdmin);
        Assert.NotEqual(version, updated.GetProperty("version").GetGuid());
        Assert.Equal(2, updated.GetProperty("intervals").GetArrayLength());
        Assert.Equal("09:00:00", updated.GetProperty("intervals")[0].GetProperty("start").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await superAdmin.PutAsJsonAsync(Url, valid)).StatusCode);
        var current = updated.GetProperty("version").GetGuid();
        foreach (var intervals in new object[]
        {
            new[] { new { dayOfWeek = "Monday", start = "10:00", end = "09:00" } },
            new[] { new { dayOfWeek = "Monday", start = "09:00", end = "09:15" } },
            new[] { new { dayOfWeek = "Monday", start = "09:00:01", end = "09:30:01" } },
            new[] { new { dayOfWeek = "Monday", start = "09:00", end = "10:00" }, new { dayOfWeek = "Monday", start = "09:30", end = "10:30" } },
            new[] { new { dayOfWeek = "Monday", start = "09:00", end = "10:00" }, new { dayOfWeek = "Monday", start = "09:00", end = "10:00" } },
            new[] { new { dayOfWeek = "Monday", start = "09:00", end = "09:00" } },
            new[] { new { dayOfWeek = "Monday", start = "23:30", end = "00:30" } },
            new[] { new { dayOfWeek = "Monday", start = "09:00" } },
            new object?[] { null },
            new[] { new { dayOfWeek = 9, start = "09:00", end = "10:00" } }
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await superAdmin.PutAsJsonAsync(Url, new { version = current, intervals })).StatusCode);
        var unchanged = await Read(admin);
        Assert.Equal(current, unchanged.GetProperty("version").GetGuid());
        Assert.Equal(2, unchanged.GetProperty("intervals").GetArrayLength());
        Assert.Equal(HttpStatusCode.NoContent, (await superAdmin.PutAsJsonAsync(Url, new { version = current, intervals = Array.Empty<object>() })).StatusCode);
        Assert.Empty((await Read(admin)).GetProperty("intervals").EnumerateArray());
        using var firstScope = factory.Services.CreateScope();
        using var secondScope = factory.Services.CreateScope();
        var firstDb = firstScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var first = await firstDb.InspectionSchedules.SingleAsync();
        var second = await secondDb.InspectionSchedules.SingleAsync();
        first.Version = Guid.NewGuid();
        second.Version = Guid.NewGuid();
        await firstDb.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondDb.SaveChangesAsync());
        Assert.Equal(first.Version, (await Read(admin)).GetProperty("version").GetGuid());
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
    private static async Task<HttpClient> Login(WebApplicationFactory<Program> factory, string phone)
    {
        var client = Client(factory);
        await Token(client);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/login", new { phone, password = Password })).StatusCode);
        await Token(client);
        return client;
    }
    private static async Task Token(HttpClient client)
    {
        using var response = await client.GetAsync("/api/auth/antiforgery");
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith("XSRF-TOKEN="));
        client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        client.DefaultRequestHeaders.Add("RequestVerificationToken", Uri.UnescapeDataString(cookie.Split(';')[0]["XSRF-TOKEN=".Length..]));
    }
    private static async Task<JsonElement> Read(HttpClient client)
    {
        using var response = await client.GetAsync(Url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
