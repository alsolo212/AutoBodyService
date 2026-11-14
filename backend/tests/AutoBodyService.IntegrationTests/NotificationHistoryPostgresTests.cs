using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Application.Notifications;
using AutoBodyService.Application.InspectionSchedule;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.Notifications;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
    private static Notification Item(string recipient, string type, Guid? appointment, DateTimeOffset created, NotificationAudience audience = NotificationAudience.Personal) => new()
    {
        Id = Guid.NewGuid(), RecipientId = recipient, AppointmentId = appointment, Audience = audience, Type = type, Title = "Title", Message = "Message",
        CreatedAtUtc = created, EventKey = Guid.NewGuid().ToString()
    };

    [PostgresFact]
    public async Task HistoryIsRecipientScopedForEveryRoleAndMutationsRequireCsrf()
    {
        var customer = await User("User"); var admin = await User("Admin"); var super = await User("SuperAdmin");
        var when = DateTimeOffset.UtcNow.AddDays(-2); var appointment = new Appointment(Guid.NewGuid(), customer.Id, when.AddDays(10), "1HGCM82633A123456", "Make", "Model", "Issue", when);
        var older = Item(customer.Id, "AppointmentCreated", appointment.Id, when);
        var newer = Item(customer.Id, "AppointmentScheduled", appointment.Id, when.AddMinutes(1));
        var adminItem = Item(admin.Id, "AdminAppointmentCreated", appointment.Id, when, NotificationAudience.Administrator);
        var superItem = Item(super.Id, "AdminAppointmentCreated", appointment.Id, when, NotificationAudience.Administrator);
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
            Assert.Single(await history.GetAsync(admin.Id, null, default));
        }
        var temporary = await User("User", true); using var restricted = await Login(temporary.Phone);
        Assert.Equal(HttpStatusCode.Forbidden, (await restricted.GetAsync("/api/notifications")).StatusCode);
    }

    [PostgresFact]
    public async Task RoleLossHidesAdministrativeHistoryAndReadMutationsWhilePersonalHistorySurvives()
    {
        var owner = await User("User"); var otherAdmin = await User("Admin"); var supervisor = await User("SuperAdmin");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await users.AddToRoleAsync((await users.FindByIdAsync(owner.Id))!, "Admin")).Succeeded);
            var date = new DateOnly(2080, 1, 1).AddDays(RandomNumberGenerator.GetInt32(0, 9000));
            var schedules = scope.ServiceProvider.GetRequiredService<IInspectionScheduleService>();
            var current = await schedules.GetAsync(default);
            Assert.Equal(ScheduleUpdateResult.Success, await schedules.UpdateAsync(current.Version, [new(date.DayOfWeek, new(9, 0), new(10, 0))], default));
            using var admin = await Login(owner.Phone);
            var slots = await admin.GetFromJsonAsync<JsonElement>($"/api/inspection-slots?date={date:yyyy-MM-dd}");
            var start = slots.GetProperty("slots")[0].GetProperty("startUtc").GetDateTimeOffset();
            using var created = await admin.PostAsJsonAsync("/api/appointments", new { inspectionStartUtc = start, vin = "1HGCM82633A123456", make = "Test", model = "Model", problemDescription = "Issue" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var appointmentId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var stored = await db.Notifications.AsNoTracking().Where(n => n.AppointmentId == appointmentId && n.RecipientId == owner.Id).ToListAsync();
            var personal = Assert.Single(stored, n => n.Type == "AppointmentCreated");
            var roleEvent = Assert.Single(stored, n => n.Type == "AdminAppointmentCreated");
            Assert.Equal(NotificationAudience.Personal, personal.Audience);
            Assert.Equal(NotificationAudience.Administrator, roleEvent.Audience);
            var ownerSuperOnly = Item(owner.Id, "SyntheticSecurityNotice", appointmentId, DateTimeOffset.UtcNow, NotificationAudience.SuperAdministrator);
            var superOnly = Item(supervisor.Id, "SyntheticSecurityNotice", appointmentId, DateTimeOffset.UtcNow, NotificationAudience.SuperAdministrator);
            var statusPersonal = Item(owner.Id, "AppointmentScheduled", appointmentId, DateTimeOffset.UtcNow, NotificationAudience.Personal);
            var reminderPersonal = Item(owner.Id, "InspectionReminder2Hours", appointmentId, DateTimeOffset.UtcNow, NotificationAudience.Personal);
            db.Notifications.AddRange(ownerSuperOnly, superOnly, statusPersonal, reminderPersonal); await db.SaveChangesAsync();
            var initial = (await admin.GetFromJsonAsync<JsonElement[]>("/api/notifications"))!;
            Assert.Contains(initial, n => n.GetProperty("id").GetGuid() == personal.Id && n.GetProperty("link").GetString() == $"/account/appointments/{appointmentId}");
            Assert.Contains(initial, n => n.GetProperty("id").GetGuid() == roleEvent.Id && n.GetProperty("link").GetString() == $"/admin/appointments/{appointmentId}");
            Assert.DoesNotContain(initial, n => n.GetProperty("id").GetGuid() == ownerSuperOnly.Id);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.PatchAsync($"/api/notifications/{ownerSuperOnly.Id}/read", null)).StatusCode);
            using (var other = await Login(otherAdmin.Phone))
                Assert.Equal(HttpStatusCode.NotFound, (await other.PatchAsync($"/api/notifications/{roleEvent.Id}/read", null)).StatusCode);
            using (var super = await Login(supervisor.Phone))
            {
                Assert.Contains((await super.GetFromJsonAsync<JsonElement[]>("/api/notifications"))!, n => n.GetProperty("id").GetGuid() == superOnly.Id);
                Assert.Equal(HttpStatusCode.NotFound, (await super.PatchAsync($"/api/notifications/{ownerSuperOnly.Id}/read", null)).StatusCode);
            }
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync("/api/auth/logout", null)).StatusCode);
            Assert.True((await users.RemoveFromRoleAsync((await users.FindByIdAsync(owner.Id))!, "Admin")).Succeeded);
            using var customer = await Login(owner.Phone);
            var visible = (await customer.GetFromJsonAsync<JsonElement[]>("/api/notifications?unread=true"))!;
            Assert.Equal(new[] { personal.Id, statusPersonal.Id, reminderPersonal.Id }.Order(), visible.Select(n => n.GetProperty("id").GetGuid()).Order());
            Assert.Equal(HttpStatusCode.NotFound, (await customer.PatchAsync($"/api/notifications/{roleEvent.Id}/read", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await customer.PatchAsync($"/api/notifications/{ownerSuperOnly.Id}/read", null)).StatusCode);
            customer.DefaultRequestHeaders.Remove("RequestVerificationToken");
            Assert.Equal(HttpStatusCode.BadRequest, (await customer.PostAsync("/api/notifications/read-all", null)).StatusCode);
            await Token(customer);
            Assert.Equal(HttpStatusCode.NoContent, (await customer.PatchAsync($"/api/notifications/{personal.Id}/read", null)).StatusCode);
            Assert.Equal(2, (await customer.GetFromJsonAsync<JsonElement[]>("/api/notifications?unread=true"))!.Length);
            Assert.Equal(HttpStatusCode.NoContent, (await customer.PostAsync("/api/notifications/read-all", null)).StatusCode);
            Assert.Empty((await customer.GetFromJsonAsync<JsonElement[]>("/api/notifications?unread=true"))!);
            Assert.Null((await db.Notifications.AsNoTracking().SingleAsync(n => n.Id == roleEvent.Id)).ReadAtUtc);
            Assert.Null((await db.Notifications.AsNoTracking().SingleAsync(n => n.Id == ownerSuperOnly.Id)).ReadAtUtc);
            Assert.NotNull((await db.Notifications.AsNoTracking().SingleAsync(n => n.Id == statusPersonal.Id)).ReadAtUtc);
            Assert.NotNull((await db.Notifications.AsNoTracking().SingleAsync(n => n.Id == reminderPersonal.Id)).ReadAtUtc);
            Assert.Equal(HttpStatusCode.NoContent, (await customer.PostAsync("/api/auth/logout", null)).StatusCode);
            Assert.True((await users.AddToRoleAsync((await users.FindByIdAsync(owner.Id))!, "Admin")).Succeeded);
            using var restored = await Login(owner.Phone);
            var restoredUnread = (await restored.GetFromJsonAsync<JsonElement[]>("/api/notifications?unread=true"))!;
            Assert.Equal(roleEvent.Id, Assert.Single(restoredUnread).GetProperty("id").GetGuid());
            Assert.Equal(HttpStatusCode.NotFound, (await restored.PatchAsync($"/api/notifications/{ownerSuperOnly.Id}/read", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await restored.PostAsync("/api/auth/logout", null)).StatusCode);
            Assert.True((await users.AddToRoleAsync((await users.FindByIdAsync(owner.Id))!, "SuperAdmin")).Succeeded);
            using var promoted = await Login(owner.Phone);
            Assert.Contains((await promoted.GetFromJsonAsync<JsonElement[]>("/api/notifications?unread=true"))!, n => n.GetProperty("id").GetGuid() == ownerSuperOnly.Id);
            Assert.Equal(HttpStatusCode.NoContent, (await promoted.PatchAsync($"/api/notifications/{ownerSuperOnly.Id}/read", null)).StatusCode);
        }
    }

    [PostgresFact]
    public async Task MigrationClassifiesKnownLegacyEventsAndRestrictsUnknownTypesWithoutDeletingHistory()
    {
        var schema = "notification_upgrade_" + Guid.NewGuid().ToString("N");
        var connectionString = Environment.GetEnvironmentVariable("AUTOBODY_IDENTITY_POSTGRES")!;
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", connection)) await create.ExecuteNonQueryAsync();
        try
        {
            var isolated = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ConnectionString;
            await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(isolated).Options);
            var migrator = db.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20261113110646_ReminderRecipientAndOutcome");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "AspNetUsers" ("Id", "Name", "PhoneNormalized", "UserName", "PhoneNumber", "MustChangePassword", "IsBlocked", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
                VALUES ('legacy-owner', 'Test', '+380000000001', '+380000000001', '+380000000001', false, false, false, false, false, false, 0);
                INSERT INTO "Notifications" ("Id", "RecipientId", "AppointmentId", "Type", "Title", "Message", "CreatedAtUtc", "ReadAtUtc", "EventKey")
                SELECT gen_random_uuid(), 'legacy-owner', NULL, t, 'Title', 'Message', '2030-01-01 00:00:00+00', NULL, 'legacy:' || t
                FROM unnest(ARRAY['AppointmentCreated','AppointmentCancelled','AppointmentScheduled','AppointmentInProgress',
                    'AppointmentAwaitingPayment','AppointmentCompleted','RepairPlanChanged','PasswordChanged',
                    'InspectionReminder24Hours','InspectionReminder2Hours','AdminAppointmentCreated','AdminAppointmentCancelled',
                    'AdminReadyForPayment','AdminContactAgreement','AdminContactPlanChanged','AdminCalendarConflict',
                    'AdminInspectionReminder30Minutes','UnknownLegacy']) AS t;
                """);
            await migrator.MigrateAsync();
            var notifications = await db.Notifications.AsNoTracking().ToDictionaryAsync(n => n.Type);
            Assert.Equal(18, notifications.Count);
            Assert.All(notifications, entry => { Assert.Equal("legacy:" + entry.Key, entry.Value.EventKey); Assert.Equal("Message", entry.Value.Message); });
            foreach (var type in new[] { "AppointmentCreated", "AppointmentCancelled", "AppointmentScheduled", "AppointmentInProgress", "AppointmentAwaitingPayment", "AppointmentCompleted", "RepairPlanChanged", "PasswordChanged", "InspectionReminder24Hours", "InspectionReminder2Hours" })
                Assert.Equal(NotificationAudience.Personal, notifications[type].Audience);
            foreach (var type in new[] { "AdminAppointmentCreated", "AdminAppointmentCancelled", "AdminReadyForPayment", "AdminContactAgreement", "AdminContactPlanChanged", "AdminCalendarConflict", "AdminInspectionReminder30Minutes" })
                Assert.Equal(NotificationAudience.Administrator, notifications[type].Audience);
            Assert.Equal(NotificationAudience.Restricted, notifications["UnknownLegacy"].Audience);
            var history = new NotificationHistory(db, TimeProvider.System);
            Assert.Equal(10, (await history.GetAsync("legacy-owner", null, default)).Count);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "AspNetUserRoles" ("UserId", "RoleId") SELECT 'legacy-owner', "Id" FROM "AspNetRoles" WHERE "Name" = 'SuperAdmin';
                """);
            var privilegedHistory = await history.GetAsync("legacy-owner", null, default);
            Assert.Equal(17, privilegedHistory.Count);
            Assert.DoesNotContain(privilegedHistory, n => n.Type == "UnknownLegacy");
            var failure = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE \"Notifications\" SET \"Audience\" = 99 WHERE \"Type\" = 'UnknownLegacy'"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task HubHttpHandshakeRejectsAnonymousCrossOriginAndQueryTokens()
    {
        using var anonymous = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/hubs/notifications")).StatusCode);
        var user = await User("User"); using var client = await Login(user.Phone);
        using var foreign = new HttpRequestMessage(HttpMethod.Get, "/hubs/notifications");
        foreign.Headers.TryAddWithoutValidation("Origin", "https://evil.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(foreign)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/hubs/notifications?access_token=example")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/hubs/notifications/negotiate", null)).StatusCode);
    }

    [PostgresFact]
    public async Task ActiveHubClosesWhenIdentityCookieExpires()
    {
        var user = await User("User");
        using var shortSessionFactory = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, options =>
            {
                options.ExpireTimeSpan = TimeSpan.FromSeconds(4);
                options.SlidingExpiration = false;
            })));
        using var client = shortSessionFactory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
        await Token(client);
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { phone = user.Phone, password = Password });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var identity = Assert.Single(login.Headers.GetValues("Set-Cookie"), value => value.StartsWith(".AspNetCore.Identity.Application="));
        var ws = shortSessionFactory.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request => request.Headers.Cookie = identity.Split(';')[0];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var socket = await ws.ConnectAsync(new Uri("wss://localhost/hubs/notifications"), timeout.Token);
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e"), WebSocketMessageType.Text, true, timeout.Token);
        var buffer = new byte[1024];
        var handshake = await socket.ReceiveAsync(buffer, timeout.Token);
        Assert.Equal(WebSocketMessageType.Text, handshake.MessageType);
        Assert.Equal("{}\u001e", Encoding.UTF8.GetString(buffer, 0, handshake.Count));
        WebSocketReceiveResult closed;
        do
        {
            closed = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
            if (closed.MessageType == WebSocketMessageType.Text)
            {
                var frames = Encoding.UTF8.GetString(buffer, 0, closed.Count).Split('\u001e', StringSplitOptions.RemoveEmptyEntries);
                foreach (var frame in frames)
                {
                    using var json = JsonDocument.Parse(frame);
                    Assert.Contains(json.RootElement.GetProperty("type").GetInt32(), new[] { 6, 7 });
                }
            }
        } while (closed.MessageType != WebSocketMessageType.Close);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/notifications")).StatusCode);
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
        Assert.Equal(NotificationAudience.Personal, notification.Audience);
        Assert.DoesNotContain(Password, notification.Message); Assert.DoesNotContain("Another-password-123!", notification.Message);
    }
}
