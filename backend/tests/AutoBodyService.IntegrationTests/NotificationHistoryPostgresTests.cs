using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Application.Notifications;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.Notifications;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AutoBodyService.IntegrationTests;

[Collection("PostgresIdentity")]
public sealed class NotificationHistoryPostgresTests(PostgresIdentityFixture fixture) : IDisposable
{
    private readonly WebApplicationFactory<Program> factory = fixture.Factory.WithWebHostBuilder(_ => { });
    private const string Password = "Example-password-123!";
    public void Dispose() => factory.Dispose();
    private HttpClient Client() => factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
    private async Task<(string Id, string Phone)> User(string role, bool temporary = false)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var phone = "+380" + RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000);
        var user = new ApplicationUser { Name = "Test", UserName = phone, PhoneNumber = phone, PhoneNormalized = phone, MustChangePassword = temporary };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
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
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/login", new { phone, password = Password })).StatusCode);
        await Token(client); return client;
    }
    private static Notification Item(string recipient, string type, Guid? appointment, DateTimeOffset created) => new()
    {
        Id = Guid.NewGuid(), RecipientId = recipient, AppointmentId = appointment, Type = type, Title = "Title", Message = "Message",
        CreatedAtUtc = created, EventKey = Guid.NewGuid().ToString()
    };

    [PostgresFact]
    public async Task HistoryIsRecipientScopedForEveryRoleAndMutationsRequireCsrf()
    {
        var customer = await User("User"); var admin = await User("Admin"); var super = await User("SuperAdmin");
        var when = DateTimeOffset.UtcNow.AddDays(-2); var appointment = new Appointment(Guid.NewGuid(), customer.Id, when.AddDays(10), "1HGCM82633A123456", "Make", "Model", "Issue", when);
        var older = Item(customer.Id, "AppointmentCreated", appointment.Id, when);
        var newer = Item(customer.Id, "AppointmentScheduled", appointment.Id, when.AddMinutes(1));
        var adminItem = Item(admin.Id, "AdminAppointmentCreated", appointment.Id, when);
        var superItem = Item(super.Id, "AdminAppointmentCreated", appointment.Id, when);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Appointments.Add(appointment); db.Notifications.AddRange(older, newer, adminItem, superItem); await db.SaveChangesAsync();
        }
        using var anonymous = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/notifications")).StatusCode);
        using var own = await Login(customer.Phone); using var actor = await Login(admin.Phone); using var supervisor = await Login(super.Phone);
        var items = await own.GetFromJsonAsync<JsonElement[]>("/api/notifications");
        Assert.Equal(new[] { newer.Id, older.Id }, items!.Select(x => x.GetProperty("id").GetGuid()));
        Assert.All(items!, x => { Assert.Equal($"/account/appointments/{appointment.Id}", x.GetProperty("link").GetString()); Assert.False(x.TryGetProperty("recipientId", out _)); Assert.False(x.TryGetProperty("eventKey", out _)); });
        var adminItems = await actor.GetFromJsonAsync<JsonElement[]>("/api/notifications");
        Assert.Single(adminItems!); Assert.Equal(adminItem.Id, adminItems![0].GetProperty("id").GetGuid());
        Assert.Equal($"/admin/appointments/{appointment.Id}", adminItems[0].GetProperty("link").GetString());
        Assert.Equal(superItem.Id, Assert.Single((await supervisor.GetFromJsonAsync<JsonElement[]>("/api/notifications"))!).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await actor.PatchAsync($"/api/notifications/{newer.Id}/read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await own.PatchAsync($"/api/notifications/{Guid.NewGuid()}/read", null)).StatusCode);
        own.DefaultRequestHeaders.Remove("RequestVerificationToken");
        Assert.Equal(HttpStatusCode.BadRequest, (await own.PatchAsync($"/api/notifications/{newer.Id}/read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await own.PostAsync("/api/notifications/read-all", null)).StatusCode);
        await Token(own);
        Assert.Equal(HttpStatusCode.NoContent, (await own.PatchAsync($"/api/notifications/{newer.Id}/read", null)).StatusCode);
        var firstRead = Assert.Single((await own.GetFromJsonAsync<JsonElement[]>("/api/notifications?unread=false"))!).GetProperty("readAtUtc").GetDateTimeOffset();
        Assert.Equal(HttpStatusCode.NoContent, (await own.PatchAsync($"/api/notifications/{newer.Id}/read", null)).StatusCode);
        Assert.Equal(firstRead, Assert.Single((await own.GetFromJsonAsync<JsonElement[]>("/api/notifications?unread=false"))!).GetProperty("readAtUtc").GetDateTimeOffset());
        Assert.Equal(older.Id, Assert.Single((await own.GetFromJsonAsync<JsonElement[]>("/api/notifications?unread=true"))!).GetProperty("id").GetGuid());
        Assert.Equal(newer.Id, Assert.Single((await own.GetFromJsonAsync<JsonElement[]>("/api/notifications?unread=false"))!).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NoContent, (await own.PostAsync("/api/notifications/read-all", null)).StatusCode);
        Assert.Empty((await own.GetFromJsonAsync<JsonElement[]>("/api/notifications?unread=true"))!);
        Assert.Single((await actor.GetFromJsonAsync<JsonElement[]>("/api/notifications?unread=true"))!);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var history = scope.ServiceProvider.GetRequiredService<INotificationHistory>();
            Assert.Null(Assert.Single(await history.GetAsync(admin.Id, false, null, default)).Link);
        }
        var temporary = await User("User", true); using var restricted = await Login(temporary.Phone);
        Assert.Equal(HttpStatusCode.Forbidden, (await restricted.GetAsync("/api/notifications")).StatusCode);
    }

    [PostgresFact]
    public async Task UniqueEventKeyIsEnforcedByPostgresAndHistorySurvivesWithoutSignalR()
    {
        var user = await User("User"); var first = Item(user.Id, "Security", null, DateTimeOffset.UtcNow);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(); db.Notifications.Add(first); await db.SaveChangesAsync();
            var duplicate = Item(user.Id, "Security", null, DateTimeOffset.UtcNow); duplicate.EventKey = first.EventKey;
            db.Notifications.Add(duplicate);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
        using var client = await Login(user.Phone);
        var stored = Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/api/notifications"))!);
        Assert.Equal(first.Id, stored.GetProperty("id").GetGuid()); Assert.Equal(JsonValueKind.Null, stored.GetProperty("link").ValueKind);
    }

    [PostgresFact]
    public async Task PasswordChangeWritesOnlySafeNotificationAndRollsBackWhenNotificationFails()
    {
        var user = await User("User"); using var client = await Login(user.Phone);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = "wrong", newPassword = "Another-password-123!" })).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION notification_password_reject() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW."Type" = 'PasswordChanged' THEN RAISE EXCEPTION 'test failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER notification_password_reject_trigger BEFORE INSERT ON "Notifications" FOR EACH ROW EXECUTE FUNCTION notification_password_reject();
            """);
        try
        {
            Assert.Equal(HttpStatusCode.InternalServerError, (await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = Password, newPassword = "Another-password-123!" })).StatusCode);
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True(await manager.CheckPasswordAsync((await manager.FindByIdAsync(user.Id))!, Password));
            Assert.Empty(await db.Notifications.AsNoTracking().Where(n => n.RecipientId == user.Id).ToListAsync());
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER notification_password_reject_trigger ON \"Notifications\"; DROP FUNCTION notification_password_reject();"); }
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = Password, newPassword = "Another-password-123!" })).StatusCode);
        var notification = Assert.Single(await db.Notifications.AsNoTracking().Where(n => n.RecipientId == user.Id).ToListAsync());
        Assert.Equal("PasswordChanged", notification.Type); Assert.Null(notification.AppointmentId);
        Assert.DoesNotContain(Password, notification.Message); Assert.DoesNotContain("Another-password-123!", notification.Message);
    }
}
