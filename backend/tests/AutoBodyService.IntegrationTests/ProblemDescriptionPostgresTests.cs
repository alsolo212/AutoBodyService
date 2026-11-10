using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AutoBodyService.Application.InspectionSchedule;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AutoBodyService.IntegrationTests;

[Collection("PostgresIdentity")]
public sealed class ProblemDescriptionPostgresTests(PostgresIdentityFixture fixture) : IDisposable
{
    private readonly WebApplicationFactory<Program> factory = fixture.Factory.WithWebHostBuilder(_ => { });
    private const string Password = "Example-password-123!";
    public void Dispose() => factory.Dispose();

    [PostgresFact]
    public async Task DirectApiRejectsMediaOnlyAndCreatesAppointmentWithWrittenProblemAndLink()
    {
        var date = new DateOnly(2080, 3, 4);
        var local = date.ToDateTime(new TimeOnly(9, 0)); var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
        var start = new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var phone = "+380" + RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000);
            var user = new ApplicationUser { Name = "Test", PhoneNormalized = phone, PhoneNumber = phone, UserName = phone };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded); Assert.True((await users.AddToRoleAsync(user, ApplicationRoles.User)).Succeeded);
            var weekly = scope.ServiceProvider.GetRequiredService<IInspectionScheduleService>(); var schedule = await weekly.GetAsync(default);
            Assert.Equal(ScheduleUpdateResult.Success, await weekly.UpdateAsync(schedule.Version, [new(date.DayOfWeek, new(9, 0), new(10, 0))], default));
            using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
            await Token(client); Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/login", new { phone, password = Password })).StatusCode); await Token(client);
            foreach (var description in new[] { "", "   ", "!!!", "...", "???!!!", ",,,;;;", "12345", "🚗🔧", "https://example.invalid/video.mp4", "https://example.invalid/photo(1).jpg", "https://example.invalid/a,b/video.mp4", "https://example.invalid/video.mp4?caption=подряпина", "https://example.invalid/video.mp4?x=1,подряпина", "https://example.invalid/%D1%84.jpg?x=a%2Cb", "Дивіться відео: https://example.invalid/video.mp4", "(https://example.invalid/a.jpg); https://example.invalid/b.mp4" })
            {
                using var rejected = await client.PostAsJsonAsync("/api/appointments", Payload(start, description));
                Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
                if (description == "!!!")
                {
                    using var problem = await rejected.Content.ReadFromJsonAsync<JsonDocument>();
                    Assert.Equal("Перевірте дані запису.", problem!.RootElement.GetProperty("title").GetString());
                }
            }
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.False(await db.Appointments.AnyAsync(a => a.UserId == user.Id));
            const string valid = "Вм’ятина на дверях https://example.invalid/video.mp4";
            using var created = await client.PostAsJsonAsync("/api/appointments", Payload(start, valid));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var appointment = await db.Appointments.AsNoTracking().SingleAsync(a => a.UserId == user.Id);
            Assert.Equal(valid, appointment.ProblemDescription);
            Assert.Single(await db.AppointmentStatusEvents.Where(e => e.AppointmentId == appointment.Id).ToListAsync());
        }
    }
    private static object Payload(DateTimeOffset start, string description) => new { inspectionStartUtc = start, vin = "1HGCM82633A123456", make = "Test", model = "Model", problemDescription = description };
    private static async Task Token(HttpClient client)
    {
        using var response = await client.GetAsync("/api/auth/antiforgery");
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), s => s.StartsWith("XSRF-TOKEN="));
        client.DefaultRequestHeaders.Remove("RequestVerificationToken"); client.DefaultRequestHeaders.Add("RequestVerificationToken", Uri.UnescapeDataString(cookie.Split(';')[0]["XSRF-TOKEN=".Length..]));
    }
}
