using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AutoBodyService.Application.InspectionSchedule;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Domain.InspectionSchedule;
using AutoBodyService.Application.Appointments;
using AutoBodyService.Infrastructure.Appointments;
using AutoBodyService.Infrastructure.InspectionSchedule;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AutoBodyService.IntegrationTests;

[Collection("PostgresIdentity")]
public sealed class CreateAppointmentPostgresTests(PostgresIdentityFixture fixture)
{
    private const string Password = "Example-password-123!";
    private const string Url = "/api/appointments";
    private static readonly DateOnly FirstDate = new(2035, 1, 1);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");

    [PostgresFact]
    public async Task AllRolesCreateOnlyOwnAppointmentsWithAtomicEventsAndValidation()
    {
        var user = await CreateUser("User", "+380000000041");
        var admin = await CreateUser("Admin", "+380000000042");
        var super = await CreateUser("SuperAdmin", "+380000000043");
        await SetWeekly(FirstDate.DayOfWeek);
        using var anonymous = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(Url, Payload(Start(FirstDate, 9, 0)))).StatusCode);
        using var userClient = await Login("+380000000041");
        using var adminClient = await Login("+380000000042");
        using var superClient = await Login("+380000000043");
        userClient.DefaultRequestHeaders.Remove("RequestVerificationToken");
        Assert.Equal(HttpStatusCode.BadRequest, (await userClient.PostAsJsonAsync(Url, Payload(Start(FirstDate, 9, 0)))).StatusCode);
        await Token(userClient);
        var forbiddenOwner = new { inspectionStartUtc = Start(FirstDate, 9, 0), vin = "1HGCM82633A123456", make = "Test", model = "Model", problemDescription = "Issue", userId = admin };
        Assert.Equal(HttpStatusCode.BadRequest, (await userClient.PostAsJsonAsync(Url, forbiddenOwner)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await userClient.PostAsJsonAsync(Url, new { inspectionStartUtc = Start(FirstDate, 9, 0), vin = "invalid", make = "Test", model = "Model", problemDescription = "Issue" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await userClient.PostAsJsonAsync(Url, new { inspectionStartUtc = Start(FirstDate, 9, 0), vin = "1HGCM82633A123456", make = "Test", model = "Model", problemDescription = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await userClient.PostAsJsonAsync(Url, Payload(Start(FirstDate, 9, 15)))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await userClient.PostAsJsonAsync(Url, Payload(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)))).StatusCode);
        var created = new List<(Guid Id, string Owner)>();
        foreach (var (client, owner, time) in new[] { (userClient, user, Start(FirstDate, 9, 0)), (adminClient, admin, Start(FirstDate, 9, 30)), (superClient, super, Start(FirstDate, 10, 0)) })
        {
            using var response = await client.PostAsJsonAsync(Url, Payload(time));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(new[] { "id", "inspectionStartUtc", "status" }, body.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToArray());
            Assert.Equal("AwaitingInspection", body.GetProperty("status").GetString());
            var id = body.GetProperty("id").GetGuid();
            Assert.EndsWith("/api/appointments/" + id, response.Headers.Location!.ToString());
            created.Add((id, owner));
        }
        Assert.Equal(HttpStatusCode.Conflict, (await adminClient.PostAsJsonAsync(Url, Payload(Start(FirstDate, 9, 0)))).StatusCode);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        foreach (var (id, owner) in created)
        {
            Assert.Equal(owner, (await db.Appointments.SingleAsync(a => a.Id == id)).UserId);
            var history = await db.AppointmentStatusEvents.SingleAsync(e => e.AppointmentId == id);
            Assert.Equal(owner, history.ActorId);
            Assert.Null(history.FromStatus);
            Assert.Equal(AppointmentStatus.AwaitingInspection, history.ToStatus);
            var notifications = await db.Notifications.Where(n => n.AppointmentId == id).ToListAsync();
            Assert.Contains(notifications, n => n.RecipientId == owner && n.Type == "AppointmentCreated");
            Assert.Contains(notifications, n => n.RecipientId == admin && n.Type == "AdminAppointmentCreated");
            Assert.Contains(notifications, n => n.RecipientId == super && n.Type == "AdminAppointmentCreated");
            Assert.Equal(3, await db.ScheduledReminders.CountAsync(r => r.AppointmentId == id));
        }
    }

    [PostgresFact]
    public async Task SimultaneousRequestsProduceOneAppointmentAndOneSetOfEvents()
    {
        await CreateUser("User", "+380000000044");
        await CreateUser("User", "+380000000045");
        var date = FirstDate.AddDays(7);
        await SetWeekly(date.DayOfWeek);
        using var first = await Login("+380000000044");
        using var second = await Login("+380000000045");
        var slot = Start(date, 10, 30);
        var responses = await Task.WhenAll(first.PostAsJsonAsync(Url, Payload(slot)), second.PostAsJsonAsync(Url, Payload(slot)));
        try
        {
            Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }, responses.Select(r => r.StatusCode).OrderBy(s => s).ToArray());
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var appointment = await db.Appointments.SingleAsync(a => a.InspectionStartUtc == slot && a.Status != AppointmentStatus.Cancelled);
            Assert.Equal(1, await db.AppointmentStatusEvents.CountAsync(e => e.AppointmentId == appointment.Id));
            Assert.Equal(1, await db.Notifications.CountAsync(n => n.AppointmentId == appointment.Id && n.Type == "AppointmentCreated"));
            Assert.Equal(3, await db.ScheduledReminders.CountAsync(r => r.AppointmentId == appointment.Id));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [PostgresFact]
    public async Task DateExceptionsControlBookingAndPastRemindersAreNotCreated()
    {
        var owner = await CreateUser("User", "+380000000046");
        var date = FirstDate.AddDays(14);
        await SetWeekly(date.DayOfWeek);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var exceptions = scope.ServiceProvider.GetRequiredService<IInspectionExceptionsService>();
        var state = await exceptions.GetAsync(date, default);
        Assert.Equal(ScheduleUpdateResult.Success, await exceptions.UpdateAsync(state.Version,
            new(date, DateOverrideMode.Weekly, null, [], [new(new TimeOnly(9, 30), SlotOverrideMode.Closed), new(new TimeOnly(11, 0), SlotOverrideMode.Open)]), date, default));
        using var client = await Login("+380000000046");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Url, Payload(Start(date, 9, 30)))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Url, Payload(Start(date, 11, 0)))).StatusCode);
        var next = date.AddDays(7);
        state = await exceptions.GetAsync(next, default);
        Assert.Equal(ScheduleUpdateResult.Success, await exceptions.UpdateAsync(state.Version,
            new(next, DateOverrideMode.Closed, null, [], []), next, default));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Url, Payload(Start(next, 9, 0)))).StatusCode);
        var clock = new FixedClock { Now = Start(date, 9, 0).AddMinutes(-45) };
        var slots = new InspectionSlotsService(exceptions, db, clock);
        var service = new CreateAppointmentService(new AppointmentCreationStore(db, slots, clock), clock);
        var result = await service.CreateAsync(new CreateAppointmentCommand(owner, Start(date, 9, 0), "1HGCM82633A123456", "Test", "Model", "Issue"), default);
        Assert.Equal(CreateAppointmentStatus.Created, result.Status);
        var reminders = await db.ScheduledReminders.Where(r => r.AppointmentId == result.Id).ToListAsync();
        var reminder = Assert.Single(reminders);
        Assert.Equal("Admin30Minutes", reminder.Type);
        Assert.Equal(Start(date, 9, 0).AddMinutes(-30), reminder.DueAtUtc);
    }

    [PostgresFact]
    public async Task NotificationFailureRollsBackAppointmentAndInitialHistory()
    {
        await CreateUser("User", "+380000000047");
        var date = FirstDate.AddDays(28);
        await SetWeekly(date.DayOfWeek);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION appointment_test_reject_notification() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'simulated notification failure'; END $$;
            CREATE TRIGGER appointment_test_reject BEFORE INSERT ON "Notifications"
            FOR EACH ROW EXECUTE FUNCTION appointment_test_reject_notification();
            """);
        try
        {
            using var client = await Login("+380000000047");
            var start = Start(date, 9, 0);
            using var response = await client.PostAsJsonAsync(Url, Payload(start));
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.False(await db.Appointments.AnyAsync(a => a.InspectionStartUtc == start));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER appointment_test_reject ON \"Notifications\"; DROP FUNCTION appointment_test_reject_notification();");
        }
    }

    private sealed class FixedClock : TimeProvider { public required DateTimeOffset Now { get; init; } public override DateTimeOffset GetUtcNow() => Now; }

    private async Task SetWeekly(DayOfWeek day)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var schedules = scope.ServiceProvider.GetRequiredService<IInspectionScheduleService>();
        var weekly = await schedules.GetAsync(default);
        Assert.Equal(ScheduleUpdateResult.Success, await schedules.UpdateAsync(weekly.Version, [new(day, new TimeOnly(9, 0), new TimeOnly(11, 0))], default));
    }
    private async Task<string> CreateUser(string role, string phone)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { Name = "Test", UserName = phone, PhoneNumber = phone, PhoneNormalized = phone };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return user.Id;
    }
    private HttpClient Client() => fixture.Factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
    private async Task<HttpClient> Login(string phone)
    {
        var client = Client(); await Token(client);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/login", new { phone, password = Password })).StatusCode);
        await Token(client); return client;
    }
    private static async Task Token(HttpClient client)
    {
        using var response = await client.GetAsync("/api/auth/antiforgery");
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), x => x.StartsWith("XSRF-TOKEN="));
        client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        client.DefaultRequestHeaders.Add("RequestVerificationToken", Uri.UnescapeDataString(cookie.Split(';')[0]["XSRF-TOKEN=".Length..]));
    }
    private static DateTimeOffset Start(DateOnly date, int hour, int minute) => new DateTimeOffset(date.ToDateTime(new TimeOnly(hour, minute)), Zone.GetUtcOffset(date.ToDateTime(new TimeOnly(hour, minute)))).ToUniversalTime();
    private static object Payload(DateTimeOffset start) => new { inspectionStartUtc = start, vin = "1HGCM82633A123456", make = "Test", model = "Model", problemDescription = "Issue" };
}
