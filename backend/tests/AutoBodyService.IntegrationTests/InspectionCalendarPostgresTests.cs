using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AutoBodyService.Application.InspectionSchedule;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AutoBodyService.IntegrationTests;

[Collection("PostgresIdentity")]
public sealed class InspectionCalendarPostgresTests(PostgresIdentityFixture fixture) : IDisposable
{
    private readonly WebApplicationFactory<Program> factory = fixture.Factory.WithWebHostBuilder(_ => { });
    private const string Password = "Example-password-123!";
    private readonly DateOnly date = new DateOnly(2088, 1, 6).AddDays(RandomNumberGenerator.GetInt32(0, 10000));
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
    public void Dispose() => factory.Dispose();
    private HttpClient Client() => factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
    private DateTimeOffset Start(int hour, int minute) => new DateTimeOffset(date.ToDateTime(new TimeOnly(hour, minute)), Zone.GetUtcOffset(date.ToDateTime(new TimeOnly(hour, minute)))).ToUniversalTime();
    private async Task<(string Id, string Phone)> User(string role)
    {
        await using var scope = factory.Services.CreateAsyncScope(); var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var phone = "+380" + RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000);
        var user = new ApplicationUser { Name = role, PhoneNormalized = phone, PhoneNumber = phone, UserName = phone };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded); Assert.True((await users.AddToRoleAsync(user, role)).Succeeded); return (user.Id, phone);
    }
    private static async Task Token(HttpClient client)
    {
        using var r = await client.GetAsync("/api/auth/antiforgery"); var cookie = Assert.Single(r.Headers.GetValues("Set-Cookie"), s => s.StartsWith("XSRF-TOKEN="));
        client.DefaultRequestHeaders.Remove("RequestVerificationToken"); client.DefaultRequestHeaders.Add("RequestVerificationToken", Uri.UnescapeDataString(cookie.Split(';')[0]["XSRF-TOKEN=".Length..]));
    }
    private async Task<HttpClient> Login(string phone)
    {
        var client = Client(); await Token(client); Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/login", new { phone, password = Password })).StatusCode); await Token(client); return client;
    }
    private async Task<Guid> Initialize(string owner)
    {
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var weekly = scope.ServiceProvider.GetRequiredService<IInspectionScheduleService>(); var current = await weekly.GetAsync(default);
        Assert.Equal(ScheduleUpdateResult.Success, await weekly.UpdateAsync(current.Version, [new(date.DayOfWeek, new(9, 0), new(10, 0))], default));
        var appt = new Appointment(Guid.NewGuid(), owner, Start(9, 0), "1HGCM82633A123456", "Марка", "Модель", "Проблема", DateTimeOffset.UtcNow);
        db.Appointments.Add(appt); await db.SaveChangesAsync(); return appt.Id;
    }
    private static async Task<JsonElement> Read(HttpResponseMessage r) { Assert.Equal(HttpStatusCode.OK, r.StatusCode); return await r.Content.ReadFromJsonAsync<JsonElement>(); }
    private static object Exception(string mode, object[] slots) => new { mode, reason = (string?)null, intervals = Array.Empty<object>(), slots };

    [PostgresFact]
    public async Task DatePreviewRejectsStaleBookingSnapshotThenPreservesAppointmentsAndHistory()
    {
        var owner = await User("User"); var admin = await User("Admin"); var super = await User("SuperAdmin"); var id = await Initialize(owner.Id);
        using var anonymous = Client(); Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/admin/inspection-calendar?date={date:yyyy-MM-dd}")).StatusCode);
        using var customer = await Login(owner.Phone); using var adminClient = await Login(admin.Phone); using var superClient = await Login(super.Phone);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync($"/api/admin/inspection-calendar?date={date:yyyy-MM-dd}")).StatusCode);
        var calendar = await Read(await adminClient.GetAsync($"/api/admin/inspection-calendar?date={date:yyyy-MM-dd}"));
        var shown = Assert.Single(calendar.EnumerateArray()); Assert.Equal(id, shown.GetProperty("id").GetGuid());
        Assert.Equal("User", shown.GetProperty("customerName").GetString()); Assert.Equal("Марка", shown.GetProperty("make").GetString());
        Assert.False(shown.TryGetProperty("vin", out _)); Assert.False(shown.TryGetProperty("problemDescription", out _));
        Assert.Equal(HttpStatusCode.OK, (await superClient.GetAsync($"/api/admin/inspection-calendar?date={date:yyyy-MM-dd}")).StatusCode);
        var schedule = await Read(await adminClient.GetAsync($"/api/admin/inspection-exceptions?date={date:yyyy-MM-dd}")); var version = schedule.GetProperty("version").GetGuid();
        const string privateReason = "private reason must stay out of audit";
        var payload = new { version, date = date, exception = new { mode = "Closed", reason = privateReason, intervals = Array.Empty<object>(), slots = Array.Empty<object>() } };
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsJsonAsync("/api/admin/inspection-exceptions/preview", payload)).StatusCode);
        var unchanged = await Read(await adminClient.PostAsJsonAsync("/api/admin/inspection-exceptions/preview", new { version, date = date, exception = Exception("Weekly", []) }));
        Assert.Empty(unchanged.GetProperty("conflicts").EnumerateArray());
        var first = await Read(await adminClient.PostAsJsonAsync("/api/admin/inspection-exceptions/preview", payload));
        Assert.Equal(id, Assert.Single(first.GetProperty("conflicts").EnumerateArray()).GetProperty("id").GetGuid());
        var token = first.GetProperty("confirmationToken").GetString();
        Assert.Equal(HttpStatusCode.Conflict, (await adminClient.PutAsJsonAsync("/api/admin/inspection-exceptions", new { version, date = date, exception = Exception("Weekly", []), confirmationToken = token })).StatusCode);
        // A real booking after preview must invalidate confirmation while the schedule lock serializes writers.
        var created = await customer.PostAsJsonAsync("/api/appointments", new { inspectionStartUtc = Start(9, 30), vin = "1HGCM82633A123456", make = "Other", model = "Car", problemDescription = "Issue" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); var secondId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await adminClient.PutAsJsonAsync("/api/admin/inspection-exceptions", new { version, date = date, exception = payload.exception, confirmationToken = token })).StatusCode);
        await using (var beforeScope = factory.Services.CreateAsyncScope())
            Assert.Empty(await beforeScope.ServiceProvider.GetRequiredService<ApplicationDbContext>().AdminAuditEvents.Where(e => e.ActorId == admin.Id).ToListAsync());
        await using (var beforeNotifications = factory.Services.CreateAsyncScope())
            Assert.Empty(await beforeNotifications.ServiceProvider.GetRequiredService<ApplicationDbContext>().Notifications.Where(n => n.AppointmentId == id && n.Type == "AdminCalendarConflict").ToListAsync());
        var second = await Read(await adminClient.PostAsJsonAsync("/api/admin/inspection-exceptions/preview", payload));
        Assert.Equal(new[] { id, secondId }.Order(), second.GetProperty("conflicts").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).Order());
        Assert.Equal(HttpStatusCode.OK, (await adminClient.PutAsJsonAsync("/api/admin/inspection-exceptions", new { version, date = date, exception = payload.exception, confirmationToken = second.GetProperty("confirmationToken").GetString() })).StatusCode);
        var after = await Read(await adminClient.GetAsync($"/api/admin/inspection-calendar?date={date:yyyy-MM-dd}")); Assert.Equal(2, after.GetArrayLength());
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(AppointmentStatus.AwaitingInspection, (await db.Appointments.SingleAsync(a => a.Id == id)).Status);
        Assert.Single(await db.AppointmentStatusEvents.Where(e => e.AppointmentId == id).ToListAsync());
        Assert.Single(await db.AppointmentStatusEvents.Where(e => e.AppointmentId == secondId).ToListAsync());
        var conflictNotifications = await db.Notifications.Where(n => (n.AppointmentId == id || n.AppointmentId == secondId) && n.Type == "AdminCalendarConflict").ToListAsync();
        Assert.Contains(conflictNotifications, n => n.AppointmentId == id && n.RecipientId == admin.Id);
        Assert.Contains(conflictNotifications, n => n.AppointmentId == secondId && n.RecipientId == admin.Id);
        Assert.Contains(conflictNotifications, n => n.AppointmentId == id && n.RecipientId == super.Id);
        Assert.All(conflictNotifications, n => Assert.DoesNotContain(privateReason, n.Message));
        var audit = Assert.Single(await db.AdminAuditEvents.Where(e => e.ActorId == admin.Id).ToListAsync());
        Assert.Equal("InspectionExceptionChanged", audit.Action);
        Assert.Equal("InspectionDate", audit.ObjectType);
        Assert.Equal(date.ToString("yyyy-MM-dd"), audit.ObjectId);
        Assert.Equal(TimeSpan.Zero, audit.OccurredAtUtc.Offset);
        using var details = JsonDocument.Parse(audit.Details);
        Assert.Equal("Closed", details.RootElement.GetProperty("after").GetProperty("mode").GetString());
        Assert.DoesNotContain(privateReason, audit.Details);
        Assert.DoesNotContain(token!, audit.Details);
        Assert.DoesNotContain("customerName", audit.Details, StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact]
    public async Task WeeklyAndIndividualSlotPreviewsFindExistingFutureInspections()
    {
        var owner = await User("User"); var admin = await User("Admin"); var id = await Initialize(owner.Id); using var client = await Login(admin.Phone);
        var weekly = await Read(await client.GetAsync("/api/admin/inspection-schedule")); var version = weekly.GetProperty("version").GetGuid();
        var weeklyPreview = await Read(await client.PostAsJsonAsync("/api/admin/inspection-schedule/preview", new { version, intervals = Array.Empty<object>() }));
        Assert.Contains(weeklyPreview.GetProperty("conflicts").EnumerateArray(), item => item.GetProperty("id").GetGuid() == id);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/admin/inspection-schedule", new { version, intervals = Array.Empty<object>() })).StatusCode);
        var rule = Exception("Weekly", [new { start = "09:00", mode = "Closed" }]);
        var slotPreview = await Read(await client.PostAsJsonAsync("/api/admin/inspection-exceptions/preview", new { version, date = date, exception = rule }));
        Assert.Equal(id, Assert.Single(slotPreview.GetProperty("conflicts").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/admin/inspection-exceptions", new { version, date = date, exception = rule, confirmationToken = slotPreview.GetProperty("confirmationToken").GetString() })).StatusCode);
        var latest = await Read(await client.GetAsync("/api/admin/inspection-schedule")); var latestVersion = latest.GetProperty("version").GetGuid();
        var finalPreview = await Read(await client.PostAsJsonAsync("/api/admin/inspection-schedule/preview", new { version = latestVersion, intervals = Array.Empty<object>() }));
        Assert.Contains(finalPreview.GetProperty("conflicts").EnumerateArray(), item => item.GetProperty("id").GetGuid() == id);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/admin/inspection-schedule", new { version = latestVersion, intervals = Array.Empty<object>(), confirmationToken = finalPreview.GetProperty("confirmationToken").GetString() })).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(AppointmentStatus.AwaitingInspection, (await db.Appointments.SingleAsync(a => a.Id == id)).Status);
        Assert.Single(await db.AppointmentStatusEvents.Where(e => e.AppointmentId == id).ToListAsync());
        Assert.Single(await db.Notifications.Where(n => n.AppointmentId == id && n.RecipientId == admin.Id && n.Type == "AdminCalendarConflict").ToListAsync());
        var events = await db.AdminAuditEvents.Where(e => e.ActorId == admin.Id).OrderBy(e => e.OccurredAtUtc).ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Equal(new[] { "InspectionExceptionChanged", "InspectionScheduleChanged" }, events.Select(e => e.Action));
        var weeklyEvent = events[1];
        Assert.Equal("InspectionSchedule", weeklyEvent.ObjectType);
        Assert.Equal("1", weeklyEvent.ObjectId);
        using var details = JsonDocument.Parse(weeklyEvent.Details);
        Assert.NotEmpty(details.RootElement.GetProperty("before").EnumerateArray());
        Assert.Empty(details.RootElement.GetProperty("after").EnumerateArray());
    }
}
