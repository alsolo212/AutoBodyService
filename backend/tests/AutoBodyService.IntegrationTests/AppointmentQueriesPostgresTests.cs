using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AutoBodyService.IntegrationTests;

[Collection("PostgresIdentity")]
public sealed class AppointmentQueriesPostgresTests(PostgresIdentityFixture fixture) : IDisposable
{
    private readonly WebApplicationFactory<Program> factory = fixture.Factory.WithWebHostBuilder(_ => { });
    public void Dispose() => factory.Dispose();
    private const string Password = "Example-password-123!";
    private const string Vin = "1HGCM82633A004352";
    private HttpClient Client() => factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });

    private async Task<(string Id, string Phone)> CreateUser(string role, string name, bool temporary = false)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var phone = "+380" + RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000);
        var user = new ApplicationUser { Name = name, UserName = phone, PhoneNumber = phone, PhoneNormalized = phone, MustChangePassword = temporary };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return (user.Id, phone);
    }
    private static async Task SignIn(HttpClient client, string phone)
    {
        using var token = await client.GetAsync("/api/auth/antiforgery");
        var cookie = Assert.Single(token.Headers.GetValues("Set-Cookie"), c => c.StartsWith("XSRF-TOKEN="));
        client.DefaultRequestHeaders.Add("RequestVerificationToken", Uri.UnescapeDataString(cookie.Split(';')[0]["XSRF-TOKEN=".Length..]));
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { phone, password = Password });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
    private async Task<Guid> AddAppointment(string ownerId, AppointmentStatus status)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var id = Guid.NewGuid();
        var date = new DateTimeOffset(2040, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(RandomNumberGenerator.GetInt32(1, 100_000_000));
        var appointment = new Appointment(id, ownerId, date, Vin, "Марка", "Модель", "Опис проблеми", DateTimeOffset.UtcNow);
        db.Appointments.Add(appointment);
        db.Entry(appointment).Property(a => a.Status).CurrentValue = status;
        await db.SaveChangesAsync();
        return id;
    }
    private static async Task<JsonElement> Read(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [PostgresFact]
    public async Task OpenApiIncludesBothReadEndpoints()
    {
        using var client = Client();
        using var response = await client.GetAsync("/openapi/v1.json");
        var paths = (await Read(response)).GetProperty("paths");
        Assert.True(paths.GetProperty("/api/appointments/my").TryGetProperty("get", out _));
        Assert.True(paths.GetProperty("/api/appointments/{id}").TryGetProperty("get", out _));
    }

    [PostgresFact]
    public async Task MyListFiltersEachStatusCategoryAndNeverReturnsAnotherOwnersOrders()
    {
        var owner = await CreateUser(ApplicationRoles.User, "Owner");
        var other = await CreateUser(ApplicationRoles.User, "Other");
        var ids = new Dictionary<AppointmentStatus, Guid>();
        foreach (var status in Enum.GetValues<AppointmentStatus>()) ids[status] = await AddAppointment(owner.Id, status);
        await AddAppointment(other.Id, AppointmentStatus.AwaitingInspection);
        using var client = Client(); await SignIn(client, owner.Phone);
        foreach (var (filter, expected) in new[] {
            ("", ids.Values.ToHashSet()),
            ("?category=active", ids.Where(p => p.Key is not AppointmentStatus.Completed and not AppointmentStatus.Cancelled).Select(p => p.Value).ToHashSet()),
            ("?category=completed", new HashSet<Guid> { ids[AppointmentStatus.Completed] }),
            ("?category=cancelled", new HashSet<Guid> { ids[AppointmentStatus.Cancelled] }) })
        {
            using var response = await client.GetAsync("/api/appointments/my" + filter + (filter.Length == 0 ? "?userId=" : "&userId=") + other.Id);
            var data = await Read(response);
            Assert.Equal(expected.Order(), data.EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).Order());
            foreach (var item in data.EnumerateArray())
            {
                Assert.Equal("*************4352", item.GetProperty("maskedVin").GetString());
                Assert.False(item.TryGetProperty("vin", out _));
                Assert.False(item.TryGetProperty("problemDescription", out _));
                Assert.Equal(JsonValueKind.Null, item.GetProperty("repairPlan").ValueKind);
            }
        }
        using var invalid = await client.GetAsync("/api/appointments/my?category=1");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [PostgresFact]
    public async Task OwnerDetailsHideAdministrativeFieldsAndForeignIdReturnsNotFound()
    {
        var owner = await CreateUser(ApplicationRoles.User, "Owner"); var other = await CreateUser(ApplicationRoles.User, "Other");
        var ownId = await AddAppointment(owner.Id, AppointmentStatus.AwaitingInspection);
        var foreignId = await AddAppointment(other.Id, AppointmentStatus.AwaitingInspection);
        using var client = Client(); await SignIn(client, owner.Phone);
        using var response = await client.GetAsync("/api/appointments/" + ownId); var data = await Read(response);
        Assert.Equal(Vin, data.GetProperty("vin").GetString()); Assert.Equal("Опис проблеми", data.GetProperty("problemDescription").GetString());
        Assert.Equal("Марка", data.GetProperty("make").GetString()); Assert.Equal("Модель", data.GetProperty("model").GetString());
        Assert.NotEqual(default, data.GetProperty("inspectionStartUtc").GetDateTimeOffset());
        Assert.Equal("AwaitingInspection", data.GetProperty("status").GetString());
        var history = Assert.Single(data.GetProperty("history").EnumerateArray());
        Assert.Equal("AwaitingInspection", history.GetProperty("toStatus").GetString());
        Assert.False(history.TryGetProperty("actorName", out _)); Assert.False(history.TryGetProperty("reason", out _));
        Assert.False(data.TryGetProperty("customerPhone", out _)); Assert.False(data.TryGetProperty("userId", out _));
        Assert.Equal(JsonValueKind.Null, data.GetProperty("repairPlan").ValueKind); Assert.Equal(JsonValueKind.Null, data.GetProperty("participants").ValueKind);
        using var foreign = await client.GetAsync("/api/appointments/" + foreignId);
        using var missing = await client.GetAsync("/api/appointments/" + Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode); Assert.Equal(missing.StatusCode, foreign.StatusCode);
    }

    [PostgresFact]
    public async Task AdministratorsCanReadForeignDetailsButMyListRemainsOwnOnly()
    {
        var owner = await CreateUser(ApplicationRoles.User, "Customer"); var id = await AddAppointment(owner.Id, AppointmentStatus.AwaitingInspection);
        foreach (var role in new[] { ApplicationRoles.Admin, ApplicationRoles.SuperAdmin })
        {
            var admin = await CreateUser(role, role); var own = await AddAppointment(admin.Id, AppointmentStatus.AwaitingInspection);
            using var client = Client(); await SignIn(client, admin.Phone);
            using var response = await client.GetAsync("/api/appointments/" + id); var data = await Read(response);
            Assert.Equal("Customer", data.GetProperty("customerName").GetString()); Assert.Equal(owner.Phone, data.GetProperty("customerPhone").GetString());
            Assert.Equal("Customer", Assert.Single(data.GetProperty("history").EnumerateArray()).GetProperty("actorName").GetString());
            using var list = await client.GetAsync("/api/appointments/my");
            Assert.Equal(own, Assert.Single((await Read(list)).EnumerateArray()).GetProperty("id").GetGuid());
            using var missing = await client.GetAsync("/api/appointments/" + Guid.NewGuid()); Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
    }

    [PostgresFact]
    public async Task EndpointsRequireAuthenticationAndRejectTemporaryPasswordSessions()
    {
        var user = await CreateUser(ApplicationRoles.User, "Temporary", temporary: true); var id = await AddAppointment(user.Id, AppointmentStatus.AwaitingInspection);
        using var client = Client();
        foreach (var path in new[] { "/api/appointments/my", "/api/appointments/" + id })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        await SignIn(client, user.Phone);
        foreach (var path in new[] { "/api/appointments/my", "/api/appointments/" + id })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
    }
}
