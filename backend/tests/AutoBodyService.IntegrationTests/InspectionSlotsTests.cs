using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AutoBodyService.Application.InspectionSchedule;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Domain.InspectionSchedule;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.InspectionSchedule;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AutoBodyService.IntegrationTests;

public sealed class InspectionSlotsTests
{
    private const string Password = "Example-password-123!";
    private static readonly DateTimeOffset InitialNow = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SlotsRespectBookingsCurrentTimeExceptionsAndKyivTransitions()
    {
        var database = Guid.NewGuid().ToString();
        var postgres = Environment.GetEnvironmentVariable("SLOTS_TEST_POSTGRES");
        var clock = new TestClock { Now = InitialNow };
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();
            services.RemoveAll<IInspectionSlotsService>();
            services.AddScoped<IInspectionSlotsService>(sp => new InspectionSlotsService(
                sp.GetRequiredService<IInspectionExceptionsService>(), sp.GetRequiredService<ApplicationDbContext>(), clock));
            services.AddDbContext<ApplicationDbContext>(options => { if (postgres is null) options.UseInMemoryDatabase(database); else options.UseNpgsql(postgres); });
        }));
        string userId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            if (postgres is null) await db.Database.EnsureCreatedAsync();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { Name = "Test", UserName = "+380000000021", PhoneNumber = "+380000000021", PhoneNormalized = "+380000000021" };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, "User")).Succeeded);
            userId = user.Id;
            var weekly = scope.ServiceProvider.GetRequiredService<IInspectionScheduleService>();
            var schedule = await weekly.GetAsync(default);
            Assert.Equal(ScheduleUpdateResult.Success, await weekly.UpdateAsync(schedule.Version,
                [new(DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(11, 0)), new(DayOfWeek.Sunday, new TimeOnly(2, 30), new TimeOnly(4, 30))], default));
            var active = AppointmentAt(userId, new(2026, 11, 9, 7, 30, 0, TimeSpan.Zero));
            var shifted = AppointmentAt(userId, new(2026, 11, 9, 8, 15, 0, TimeSpan.Zero));
            var cancelled = AppointmentAt(userId, new(2026, 11, 9, 7, 0, 0, TimeSpan.Zero));
            db.Appointments.AddRange(active, shifted, cancelled);
            db.Entry(cancelled).Property(x => x.Status).CurrentValue = AppointmentStatus.Cancelled;
            await db.SaveChangesAsync();
        }
        if (postgres is not null)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Appointments.Add(AppointmentAt(userId, new DateTimeOffset(2026, 11, 9, 7, 30, 0, TimeSpan.Zero)));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.IsType<Npgsql.PostgresException>(error.InnerException);
            Assert.Equal("23505", ((Npgsql.PostgresException)error.InnerException!).SqlState);
        }
        using var anonymous = Client(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/inspection-slots?date=2026-11-09")).StatusCode);
        using var client = await Login(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/inspection-slots")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/inspection-slots?date=2026-02-30")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/inspection-slots?date=0001-01-01")).StatusCode);
        var day = await Read(client, "2026-11-09");
        Assert.Equal("Europe/Kyiv", day.GetProperty("timeZone").GetString());
        Assert.Equal(new[] { "2026-11-09T07:00:00+00:00" }, Starts(day));
        Assert.Equal("2026-11-09T07:30:00+00:00", day.GetProperty("slots")[0].GetProperty("endUtc").GetString());
        Assert.Empty(Starts(await Read(client, "2026-11-10")));
        clock.Now = new(2026, 11, 9, 7, 0, 0, TimeSpan.Zero);
        Assert.Empty(Starts(await Read(client, "2026-11-09")));
        clock.Now = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        Assert.Empty(Starts(await Read(client, "2026-11-09")));
        clock.Now = InitialNow;
        using (var scope = factory.Services.CreateScope())
        {
            var exceptions = scope.ServiceProvider.GetRequiredService<IInspectionExceptionsService>();
            var date = new DateOnly(2026, 11, 16);
            var state = await exceptions.GetAsync(date, default);
            Assert.Equal(ScheduleUpdateResult.Success, await exceptions.UpdateAsync(state.Version,
                new(date, DateOverrideMode.Weekly, null, [], [new(new TimeOnly(9, 30), SlotOverrideMode.Closed)]), date, default));
            date = new DateOnly(2026, 11, 23);
            state = await exceptions.GetAsync(date, default);
            Assert.Equal(ScheduleUpdateResult.Success, await exceptions.UpdateAsync(state.Version,
                new(date, DateOverrideMode.Closed, null, [], []), date, default));
        }
        Assert.Equal(3, Starts(await Read(client, "2026-11-16")).Length);
        Assert.Empty(Starts(await Read(client, "2026-11-23")));
        var spring = await Read(client, "2026-03-29");
        Assert.Equal(new[] { "2026-03-29T01:00:00+00:00" }, Starts(spring));
        var fall = await Read(client, "2026-10-25");
        Assert.Equal(new[] { "2026-10-24T23:30:00+00:00", "2026-10-25T00:00:00+00:00", "2026-10-25T01:00:00+00:00", "2026-10-25T01:30:00+00:00", "2026-10-25T02:00:00+00:00" }, Starts(fall));
        foreach (var slot in fall.GetProperty("slots").EnumerateArray())
            Assert.Equal(TimeSpan.FromMinutes(30), slot.GetProperty("endUtc").GetDateTimeOffset() - slot.GetProperty("startUtc").GetDateTimeOffset());
        Assert.Empty(Starts(await Read(client, "9998-12-31")));
    }

    private sealed class TestClock : TimeProvider { public DateTimeOffset Now { get; set; } public override DateTimeOffset GetUtcNow() => Now; }
    private static Appointment AppointmentAt(string userId, DateTimeOffset start) => new(Guid.NewGuid(), userId, start, "1HGCM82633A123456", "Test", "Model", "Test problem", InitialNow);
    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
    private static async Task<HttpClient> Login(WebApplicationFactory<Program> factory)
    {
        var client = Client(factory);
        using var csrf = await client.GetAsync("/api/auth/antiforgery");
        var cookie = Assert.Single(csrf.Headers.GetValues("Set-Cookie"), value => value.StartsWith("XSRF-TOKEN="));
        client.DefaultRequestHeaders.Add("RequestVerificationToken", Uri.UnescapeDataString(cookie.Split(';')[0]["XSRF-TOKEN=".Length..]));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/login", new { phone = "+380000000021", password = Password })).StatusCode);
        return client;
    }
    private static async Task<JsonElement> Read(HttpClient client, string date)
    {
        using var response = await client.GetAsync("/api/inspection-slots?date=" + date);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    private static string?[] Starts(JsonElement result) => result.GetProperty("slots").EnumerateArray().Select(s => s.GetProperty("startUtc").GetString()).ToArray();
}
