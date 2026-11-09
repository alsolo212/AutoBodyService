using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using AutoBodyService.Application.Appointments;
using AutoBodyService.Application.InspectionSchedule;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Infrastructure.Appointments;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AutoBodyService.IntegrationTests;

[Collection("PostgresIdentity")]
public sealed class CancelAppointmentPostgresTests(PostgresIdentityFixture fixture) : IDisposable
{
    private readonly WebApplicationFactory<Program> factory = fixture.Factory.WithWebHostBuilder(_ => { });
    private const string Password = "Example-password-123!";
    private static readonly DateTimeOffset BaseTime = new(2050, 1, 3, 7, 0, 0, TimeSpan.Zero);
    public void Dispose() => factory.Dispose();
    private HttpClient Client() => factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
    private async Task<(string Id, string Phone)> User(string role)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var phone = "+380" + RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000);
        var user = new ApplicationUser { Name = "Test", PhoneNormalized = phone, PhoneNumber = phone, UserName = phone };
        Assert.True((await manager.CreateAsync(user, Password)).Succeeded);
        Assert.True((await manager.AddToRoleAsync(user, role)).Succeeded);
        return (user.Id, phone);
    }
    private static async Task Token(HttpClient client)
    {
        using var response = await client.GetAsync("/api/auth/antiforgery");
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), s => s.StartsWith("XSRF-TOKEN="));
        client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        client.DefaultRequestHeaders.Add("RequestVerificationToken", Uri.UnescapeDataString(cookie.Split(';')[0]["XSRF-TOKEN=".Length..]));
    }
    private async Task<HttpClient> Login(string phone)
    {
        var client = Client(); await Token(client);
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { phone, password = Password });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); await Token(client); return client;
    }
    private async Task<Guid> Add(string owner, DateTimeOffset start, AppointmentStatus status = AppointmentStatus.AwaitingInspection)
    {
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var appointment = new Appointment(Guid.NewGuid(), owner, start, "1HGCM82633A123456", "Test", "Model", "Issue", start.AddDays(-2));
        db.Appointments.Add(appointment); db.Entry(appointment).Property(a => a.Status).CurrentValue = status;
        await db.SaveChangesAsync(); return appointment.Id;
    }
    private static string Url(Guid id) => $"/api/appointments/{id}/cancel";

    [PostgresFact]
    public async Task CancellationPersistsHistoryNotificationsAndReleasesSlotForBooking()
    {
        var owner = await User(ApplicationRoles.User); var admin = await User(ApplicationRoles.Admin); var super = await User(ApplicationRoles.SuperAdmin);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(BaseTime, zone).DateTime);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var schedules = scope.ServiceProvider.GetRequiredService<IInspectionScheduleService>(); var state = await schedules.GetAsync(default);
            Assert.Equal(ScheduleUpdateResult.Success, await schedules.UpdateAsync(state.Version, [new(date.DayOfWeek, new TimeOnly(9, 0), new TimeOnly(10, 0))], default));
        }
        var id = await Add(owner.Id, BaseTime);
        using var client = await Login(owner.Phone);
        var before = await client.GetFromJsonAsync<SlotsResponse>($"/api/inspection-slots?date={date:yyyy-MM-dd}");
        Assert.DoesNotContain(before!.Slots, slot => slot.StartUtc == BaseTime);
        using var response = await client.PostAsync(Url(id), null); Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var again = await client.PostAsync(Url(id), null); Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var after = await client.GetFromJsonAsync<SlotsResponse>($"/api/inspection-slots?date={date:yyyy-MM-dd}");
        Assert.Contains(after!.Slots, slot => slot.StartUtc == BaseTime);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(AppointmentStatus.Cancelled, (await db.Appointments.SingleAsync(a => a.Id == id)).Status);
            var events = await db.AppointmentStatusEvents.Where(e => e.AppointmentId == id).ToListAsync();
            Assert.Equal(2, events.Count); var cancel = Assert.Single(events, e => e.ToStatus == AppointmentStatus.Cancelled);
            Assert.Equal(AppointmentStatus.AwaitingInspection, cancel.FromStatus); Assert.Equal(owner.Id, cancel.ActorId); Assert.Null(cancel.Reason);
            var notifications = await db.Notifications.Where(n => n.AppointmentId == id).ToListAsync();
            Assert.Contains(notifications, n => n.RecipientId == owner.Id && n.Type == "AppointmentCancelled");
            Assert.Contains(notifications, n => n.RecipientId == admin.Id && n.Type == "AdminAppointmentCancelled");
            Assert.Contains(notifications, n => n.RecipientId == super.Id && n.Type == "AdminAppointmentCancelled");
            Assert.Equal(notifications.Count, notifications.Select(n => n.EventKey).Distinct().Count());
        }
        using var booking = await client.PostAsJsonAsync("/api/appointments", new { inspectionStartUtc = BaseTime, vin = "1HGCM82633A123456", make = "Test", model = "Model", problemDescription = "Issue" });
        Assert.Equal(HttpStatusCode.Created, booking.StatusCode);
        await using var verifyScope = factory.Services.CreateAsyncScope();
        Assert.Equal(2, await verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Appointments.CountAsync(a => a.InspectionStartUtc == BaseTime));
    }
    private sealed record SlotsResponse(IReadOnlyList<Slot> Slots);
    private sealed record Slot(DateTimeOffset StartUtc, DateTimeOffset EndUtc);

    [PostgresFact]
    public async Task AllRolesAreLimitedToOwnFutureAwaitingInspections()
    {
        var other = await User(ApplicationRoles.User); var foreign = await Add(other.Id, BaseTime.AddDays(30));
        foreach (var role in new[] { ApplicationRoles.User, ApplicationRoles.Admin, ApplicationRoles.SuperAdmin })
        {
            var owner = await User(role); using var client = await Login(owner.Phone);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(Url(foreign), null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(Url(Guid.NewGuid()), null)).StatusCode);
            var future = await Add(owner.Id, BaseTime.AddDays(31 + Array.IndexOf(new[] { "User", "Admin", "SuperAdmin" }, role)));
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync(Url(future), null)).StatusCode);
            var past = await Add(owner.Id, new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(Array.IndexOf(new[] { "User", "Admin", "SuperAdmin" }, role)));
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(Url(past), null)).StatusCode);
            foreach (var status in new[] { AppointmentStatus.Scheduled, AppointmentStatus.InProgress, AppointmentStatus.AwaitingPayment, AppointmentStatus.Completed, AppointmentStatus.Cancelled })
            {
                var id = await Add(owner.Id, BaseTime.AddDays(40 + Array.IndexOf(new[] { "User", "Admin", "SuperAdmin" }, role)).AddMinutes((int)status), status);
                Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(Url(id), null)).StatusCode);
                await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Assert.Equal(status, (await db.Appointments.SingleAsync(a => a.Id == id)).Status);
                Assert.Single(await db.AppointmentStatusEvents.Where(e => e.AppointmentId == id).ToListAsync());
            }
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    [PostgresFact]
    public async Task CancellationUsesServerClockAtTheInspectionBoundary()
    {
        var owner = await User(ApplicationRoles.User);
        var at = BaseTime.AddDays(55); var exactId = await Add(owner.Id, at); var beforeId = await Add(owner.Id, at.AddHours(1));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var service = new CancelAppointmentService(db, new FixedClock(at));
            Assert.Equal(CancelAppointmentResult.Conflict, await service.CancelOwnAsync(exactId, owner.Id, default));
        }
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var service = new CancelAppointmentService(db, new FixedClock(at.AddHours(1).AddTicks(-10)));
            Assert.Equal(CancelAppointmentResult.Cancelled, await service.CancelOwnAsync(beforeId, owner.Id, default));
        }
        await using var verifyScope = factory.Services.CreateAsyncScope(); var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(AppointmentStatus.AwaitingInspection, (await verify.Appointments.SingleAsync(a => a.Id == exactId)).Status);
        Assert.Single(await verify.AppointmentStatusEvents.Where(e => e.AppointmentId == exactId).ToListAsync());
        Assert.Equal(AppointmentStatus.Cancelled, (await verify.Appointments.SingleAsync(a => a.Id == beforeId)).Status);
    }

    [PostgresFact]
    public async Task AuthenticationAndAntiforgeryAreRequired()
    {
        var owner = await User(ApplicationRoles.User); var id = await Add(owner.Id, BaseTime.AddDays(50));
        using var anonymous = Client(); Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(Url(id), null)).StatusCode);
        using var client = await Login(owner.Phone); client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(Url(id), null)).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(AppointmentStatus.AwaitingInspection, (await db.Appointments.SingleAsync(a => a.Id == id)).Status);
    }

    [PostgresFact]
    public async Task ConcurrentCancellationProducesOneTransitionAndPreventsStaleUpdates()
    {
        var owner = await User(ApplicationRoles.User); var id = await Add(owner.Id, BaseTime.AddDays(60));
        using var first = await Login(owner.Phone); using var second = await Login(owner.Phone);
        await using var staleScope = factory.Services.CreateAsyncScope(); var staleDb = staleScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stale = await staleDb.Appointments.SingleAsync(a => a.Id == id);
        var results = await Task.WhenAll(first.PostAsync(Url(id), null), second.PostAsync(Url(id), null));
        Assert.Equal(new[] { HttpStatusCode.NoContent, HttpStatusCode.Conflict }.Order(), results.Select(r => r.StatusCode).Order());
        foreach (var result in results) result.Dispose();
        staleDb.Entry(stale).Property(a => a.Status).CurrentValue = AppointmentStatus.Scheduled;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleDb.SaveChangesAsync());
        await using var verifyScope = factory.Services.CreateAsyncScope(); var db = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(AppointmentStatus.Cancelled, (await db.Appointments.SingleAsync(a => a.Id == id)).Status);
        Assert.Equal(1, await db.AppointmentStatusEvents.CountAsync(e => e.AppointmentId == id && e.ToStatus == AppointmentStatus.Cancelled));
        Assert.Equal(1, await db.Notifications.CountAsync(n => n.AppointmentId == id && n.RecipientId == owner.Id && n.Type == "AppointmentCancelled"));
    }

    [PostgresFact]
    public async Task NotificationFailureRollsBackStatusAndHistory()
    {
        var owner = await User(ApplicationRoles.User); var id = await Add(owner.Id, BaseTime.AddDays(70)); using var client = await Login(owner.Phone);
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION cancel_test_reject_notification() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW."Type" = 'AppointmentCancelled' THEN RAISE EXCEPTION 'simulated failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER cancel_test_reject BEFORE INSERT ON "Notifications" FOR EACH ROW EXECUTE FUNCTION cancel_test_reject_notification();
            """);
        try
        {
            Assert.Equal(HttpStatusCode.InternalServerError, (await client.PostAsync(Url(id), null)).StatusCode);
            Assert.Equal(AppointmentStatus.AwaitingInspection, (await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == id)).Status);
            Assert.Single(await db.AppointmentStatusEvents.Where(e => e.AppointmentId == id).ToListAsync());
            Assert.Empty(await db.Notifications.Where(n => n.AppointmentId == id).ToListAsync());
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER cancel_test_reject ON \"Notifications\"; DROP FUNCTION cancel_test_reject_notification();"); }
    }
}
