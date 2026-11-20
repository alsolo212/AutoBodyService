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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace AutoBodyService.IntegrationTests;

[Collection("PostgresIdentity")]
public sealed class BookingRulesPostgresTests(PostgresIdentityFixture fixture)
{
    private const string Password = "Example-password-123!";
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
    private static DateTimeOffset Start(DateOnly day, int hour, int minute = 0)
    {
        var local = day.ToDateTime(new TimeOnly(hour, minute));
        return new DateTimeOffset(local, Zone.GetUtcOffset(local)).ToUniversalTime();
    }
    private WebApplicationFactory<Program> Factory(DateTimeOffset now) => fixture.Factory.WithWebHostBuilder(builder =>
        builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new FakeTimeProvider(now)))));
    private static async Task<(string Id, string Phone)> User(WebApplicationFactory<Program> factory, string role, string? phone = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        phone ??= "+380" + RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000);
        var user = new ApplicationUser { Name = "Тест", UserName = phone, PhoneNumber = phone, PhoneNormalized = phone };
        Assert.True((await manager.CreateAsync(user, Password)).Succeeded);
        Assert.True((await manager.AddToRoleAsync(user, role)).Succeeded);
        return (user.Id, phone);
    }
    private static async Task<HttpClient> Login(WebApplicationFactory<Program> factory, string phone)
    {
        var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
        await Token(client);
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
    private static async Task OpenWeek(WebApplicationFactory<Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var schedules = scope.ServiceProvider.GetRequiredService<IInspectionScheduleService>();
        var current = await schedules.GetAsync(default);
        Assert.Equal(ScheduleUpdateResult.Success, await schedules.UpdateAsync(current.Version,
            Enum.GetValues<DayOfWeek>().Select(day => new WeeklyInterval(day, new TimeOnly(9, 0), new TimeOnly(18, 0))).ToArray(), default));
    }
    private static object Payload(DateTimeOffset time, string? vin = null) => new { inspectionStartUtc = time, vin, make = "Test", model = "Car", problemDescription = "Пошкоджено двері" };
    private static object Guest(DateTimeOffset time, string phone) => new { inspectionStartUtc = time, customerName = "Тестовий клієнт", customerPhone = phone, vin = (string?)null, make = "Test", model = "Car", problemDescription = "Пошкоджено двері" };

    [PostgresFact]
    public async Task KyivMonthBoundaryAndConcurrentSlotAreEnforcedWithOptionalVin()
    {
        using var factory = Factory(new DateTimeOffset(2044, 12, 29, 12, 0, 0, TimeSpan.Zero)); await OpenWeek(factory);
        var first = await User(factory, "User"); var second = await User(factory, "User");
        using var a = await Login(factory, first.Phone); using var b = await Login(factory, second.Phone);
        var last = Start(new DateOnly(2045, 1, 31), 9);
        Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsJsonAsync("/api/appointments", Payload(Start(new DateOnly(2045, 2, 1), 9)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync("/api/appointments", Payload(last, "INVALID"))).StatusCode);
        var results = await Task.WhenAll(a.PostAsJsonAsync("/api/appointments", Payload(last)), b.PostAsJsonAsync("/api/appointments", Payload(last)));
        Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }, results.Select(x => x.StatusCode).Order().ToArray());
        var id = (await results.Single(x => x.StatusCode == HttpStatusCode.Created).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        foreach (var result in results) result.Dispose();
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var appointment = await db.Appointments.SingleAsync(x => x.Id == id);
        Assert.Null(appointment.Vin); Assert.Equal(last, appointment.InspectionStartUtc);
        Assert.Equal(1, await db.Appointments.CountAsync(x => x.InspectionStartUtc == last && x.Status != AppointmentStatus.Cancelled));
    }

    [PostgresFact]
    public async Task ParallelPersonalRequestsCannotExceedTenPerKyivDay()
    {
        using var factory = Factory(new DateTimeOffset(2044, 12, 29, 12, 0, 0, TimeSpan.Zero)); await OpenWeek(factory);
        var owner = await User(factory, "User"); using var client = await Login(factory, owner.Phone);
        var date = new DateOnly(2045, 1, 10);
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => client.PostAsJsonAsync("/api/appointments", Payload(Start(date, 9 + i / 2, i % 2 * 30)))));
        Assert.Equal(10, responses.Count(x => x.StatusCode == HttpStatusCode.Created));
        Assert.Equal(2, responses.Count(x => x.StatusCode == HttpStatusCode.TooManyRequests));
        foreach (var response in responses) response.Dispose();
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(10, await db.Appointments.CountAsync(x => x.UserId == owner.Id));
    }

    [PostgresFact]
    public async Task DailyLimitUsesKyivDayAcrossDstAndCountsCancelledRequests()
    {
        var now = new DateTimeOffset(2028, 10, 29, 21, 30, 0, TimeSpan.Zero);
        using var factory = Factory(now); await OpenWeek(factory);
        var owner = await User(factory, "User"); using var client = await Login(factory, owner.Phone);
        var localDay = new DateOnly(2028, 10, 29);
        var dayStart = Start(localDay, 0);
        Assert.Equal(TimeSpan.FromHours(25), Start(localDay.AddDays(1), 0) - dayStart);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            for (var i = 0; i < 10; i++)
            {
                var created = i == 0 ? dayStart.AddTicks(-1) : dayStart.AddMinutes(i);
                var item = new Appointment(Guid.NewGuid(), owner.Id, Start(new DateOnly(2028, 11, 10 + i), 9), null, "Test", "Car", "Пошкоджено двері", created);
                if (i == 1) db.Entry(item).Property(x => x.Status).CurrentValue = AppointmentStatus.Cancelled;
                db.Appointments.Add(item);
            }
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/appointments", Payload(Start(new DateOnly(2028, 10, 30), 9)))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/appointments", Payload(Start(new DateOnly(2028, 10, 30), 9, 30)))).StatusCode);
    }

    [PostgresFact]
    public async Task StaffGuestBookingIsSeparateFromPersonalLimitAndLaterRegistration()
    {
        using var factory = Factory(new DateTimeOffset(2044, 12, 30, 12, 0, 0, TimeSpan.Zero)); await OpenWeek(factory);
        var admin = await User(factory, "Admin"); var supervisor = await User(factory, "SuperAdmin"); var customer = await User(factory, "User");
        using var staff = await Login(factory, admin.Phone); using var super = await Login(factory, supervisor.Phone); using var regular = await Login(factory, customer.Phone);
        await using (var seedScope = factory.Services.CreateAsyncScope())
        {
            var seed = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            for (var i = 0; i < 10; i++)
            {
                seed.Appointments.Add(new Appointment(Guid.NewGuid(), admin.Id, Start(new DateOnly(2045, 1, 19), 9 + i / 2, i % 2 * 30), null, "Test", "Car", "Пошкоджено двері", new DateTimeOffset(2044, 12, 30, 10, 0, 0, TimeSpan.Zero)));
                seed.Appointments.Add(new Appointment(Guid.NewGuid(), supervisor.Id, Start(new DateOnly(2045, 1, 20), 9 + i / 2, i % 2 * 30), null, "Test", "Car", "Пошкоджено двері", new DateTimeOffset(2044, 12, 30, 10, 0, 0, TimeSpan.Zero)));
            }
            await seed.SaveChangesAsync();
        }
        var guestPhone = "+380" + RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000);
        var slot = Start(new DateOnly(2045, 1, 18), 9);
        using var anonymous = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/admin/appointments/guest", Guest(slot, guestPhone))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await regular.PostAsJsonAsync("/api/admin/appointments/guest", Guest(slot, guestPhone))).StatusCode);
        staff.DefaultRequestHeaders.Remove("RequestVerificationToken");
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync("/api/admin/appointments/guest", Guest(slot, guestPhone))).StatusCode);
        await Token(staff);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync("/api/admin/appointments/guest", Guest(slot, "invalid"))).StatusCode);
        using var created = await staff.PostAsJsonAsync("/api/admin/appointments/guest", Guest(slot, guestPhone));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await super.PostAsJsonAsync("/api/admin/appointments/guest", Guest(slot, guestPhone))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await super.PostAsJsonAsync("/api/admin/appointments/guest", Guest(Start(new DateOnly(2045, 1, 18), 9, 30), "+380000000099"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await staff.PostAsJsonAsync("/api/appointments", Payload(Start(new DateOnly(2045, 1, 18), 10)))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await super.PostAsJsonAsync("/api/appointments", Payload(Start(new DateOnly(2045, 1, 18), 10)))).StatusCode);
        var board = await staff.GetFromJsonAsync<JsonElement>("/api/admin/appointments?search=" + Uri.EscapeDataString(guestPhone));
        Assert.Contains(board.GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetGuid() == id);
        var dashboard = await staff.GetFromJsonAsync<JsonElement>("/api/admin/dashboard");
        Assert.Contains(dashboard.GetProperty("newAppointments").EnumerateArray(), x => x.GetProperty("id").GetGuid() == id);
        var details = await staff.GetFromJsonAsync<JsonElement>($"/api/appointments/{id}");
        Assert.Equal(guestPhone, details.GetProperty("customerPhone").GetString()); Assert.Equal("Тестовий клієнт", details.GetProperty("customerName").GetString());
        var calendar = await staff.GetFromJsonAsync<JsonElement[]>("/api/admin/inspection-calendar?date=2045-01-18");
        Assert.Contains(calendar!, x => x.GetProperty("id").GetGuid() == id && x.GetProperty("customerName").GetString() == "Тестовий клієнт");
        Assert.Equal(HttpStatusCode.NotFound, (await regular.GetAsync($"/api/appointments/{id}")).StatusCode);
        var mine = await staff.GetFromJsonAsync<JsonElement[]>("/api/appointments/my"); Assert.DoesNotContain(mine!, x => x.GetProperty("id").GetGuid() == id);
        var later = await User(factory, "User", guestPhone); using var guestAccount = await Login(factory, later.Phone);
        Assert.Empty((await guestAccount.GetFromJsonAsync<JsonElement[]>("/api/appointments/my"))!);
        Assert.Equal(HttpStatusCode.NoContent, (await staff.PostAsJsonAsync($"/api/admin/appointments/{id}/cancel", new { version = details.GetProperty("version").GetUInt32(), confirmed = true, reason = "Тестова причина" })).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var record = await db.Appointments.SingleAsync(x => x.Id == id);
        Assert.Null(record.UserId); Assert.Equal(admin.Id, record.CreatedById); Assert.Null(record.Vin);
        Assert.Equal(admin.Id, (await db.AppointmentStatusEvents.SingleAsync(x => x.AppointmentId == id && x.FromStatus == null)).ActorId);
        Assert.DoesNotContain(await db.Notifications.Where(x => x.AppointmentId == id).ToListAsync(), x => x.Audience == AutoBodyService.Infrastructure.Notifications.NotificationAudience.Personal);
        Assert.DoesNotContain(await db.ScheduledReminders.Where(x => x.AppointmentId == id).ToListAsync(), x => x.Type.StartsWith("Client"));
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.True((await users.RemoveFromRoleAsync((await users.FindByIdAsync(admin.Id))!, "Admin")).Succeeded);
        Assert.Equal(HttpStatusCode.Unauthorized, (await staff.PostAsJsonAsync("/api/admin/appointments/guest", Guest(Start(new DateOnly(2045, 1, 18), 10), guestPhone))).StatusCode);
    }
}
