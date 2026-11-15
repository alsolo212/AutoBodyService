using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.Notifications;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AutoBodyService.IntegrationTests;

[Collection("PostgresIdentity")]
public sealed class AdminAppointmentCommandsPostgresTests(PostgresIdentityFixture fixture) : IDisposable
{
    private readonly WebApplicationFactory<Program> factory = fixture.Factory.WithWebHostBuilder(_ => { });
    private const string Password = "Example-password-123!";
    private static readonly DateTimeOffset PlanStart = new(2050, 2, 1, 10, 0, 0, TimeSpan.Zero);
    public void Dispose() => factory.Dispose();

    private async Task<(string Id, string Phone)> User(string role)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var phone = "+380" + RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000);
        var user = new ApplicationUser { Name = "Test", UserName = phone, PhoneNumber = phone, PhoneNormalized = phone };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return (user.Id, phone);
    }
    private HttpClient Client() => factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
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
    private async Task<Guid> Add(string owner, AppointmentStatus state = AppointmentStatus.AwaitingInspection, bool past = false)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var slot = (past ? new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero) : PlanStart).AddSeconds(RandomNumberGenerator.GetInt32(1, 10_000_000));
        var appointment = new Appointment(Guid.NewGuid(), owner, slot, "1HGCM82633A123456", "Test", "Model", "Issue", slot.AddDays(-2));
        if (state != AppointmentStatus.AwaitingInspection)
        {
            appointment.Schedule(new RepairPlan(appointment.Id, 100m, PlanStart, PlanStart.AddDays(1), DateTimeOffset.UtcNow.AddDays(-1)), true);
            if (state is AppointmentStatus.InProgress or AppointmentStatus.AwaitingPayment or AppointmentStatus.Completed) appointment.StartRepair();
            if (state is AppointmentStatus.AwaitingPayment or AppointmentStatus.Completed) appointment.FinishRepair();
            if (state == AppointmentStatus.Completed) appointment.Complete(true, true);
            if (state == AppointmentStatus.Cancelled) appointment.CancelByAdmin(true, "Test reason");
        }
        db.Appointments.Add(appointment); await db.SaveChangesAsync(); return appointment.Id;
    }
    private static string Url(Guid id) => $"/api/admin/appointments/{id}";
    private static object Plan(uint version, bool inspected = true, decimal price = 100m) => new { version, price, plannedStartUtc = PlanStart, estimatedFinishUtc = PlanStart.AddDays(1), inspectionPerformed = inspected };
    private static async Task<JsonElement> Details(HttpClient client, Guid id)
    {
        using var response = await client.GetAsync($"/api/appointments/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    private static async Task<uint> Version(HttpClient client, Guid id) => (await Details(client, id)).GetProperty("version").GetUInt32();
    private static Task<HttpResponseMessage> Transition(HttpClient client, Guid id, uint version, string target, bool inspection = false, bool payment = false, bool handover = false) =>
        client.PostAsJsonAsync(Url(id) + "/transition", new { version, target, inspectionPerformed = inspection, paymentReceived = payment, vehicleHandedOver = handover });
    private static Task<HttpResponseMessage> Cancel(HttpClient client, Guid id, uint version, bool confirmed = true, string reason = "Customer request") =>
        client.PostAsJsonAsync(Url(id) + "/cancel", new { version, confirmed, reason });

    [PostgresFact]
    public async Task ExactRepairPriceRoundTripsAndUnrepresentableInputIsAtomic()
    {
        var owner = await User("User"); var admin = await User("Admin"); var id = await Add(owner.Id);
        using var actor = await Login(admin.Phone); using var customer = await Login(owner.Phone);
        async Task<HttpResponseMessage> Save(uint version, string literal)
        {
            var body = $"{{\"version\":{version},\"price\":{literal},\"plannedStartUtc\":\"2050-02-01T10:00:00Z\",\"estimatedFinishUtc\":\"2050-02-02T10:00:00Z\",\"inspectionPerformed\":true}}";
            return await actor.PutAsync(Url(id) + "/repair-plan", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        }
        foreach (var literal in new[] { "0", "0.01", "1", "123.45", "999999.99", "9007199254740993.01", "79228162514264337593543950335", "0.0000000000000000000000000001", "1e-28", "1e2", "1.00000000000000000000000000000", "7922816251426433759354395033.5" })
        {
            var version = await Version(actor, id);
            Assert.Equal(HttpStatusCode.NoContent, (await Save(version, literal)).StatusCode);
            var expected = decimal.Parse(literal, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
            var detail = await Details(actor, id);
            Assert.Equal(expected, detail.GetProperty("repairPlan").GetProperty("agreedPrice").GetDecimal());
            Assert.Equal(expected, decimal.Parse(detail.GetProperty("repairPlan").GetProperty("agreedPriceExact").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(expected, (await db.Set<RepairPlan>().AsNoTracking().SingleAsync(x => x.AppointmentId == id)).AgreedPrice);
        }
        var before = await Details(actor, id); var beforeVersion = before.GetProperty("version").GetUInt32();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var auditCount = await db.AdminAuditEvents.CountAsync(x => x.ObjectId == id.ToString());
            var historyCount = await db.AppointmentStatusEvents.CountAsync(x => x.AppointmentId == id);
            foreach (var invalid in new[] { "0.12345678901234567890123456789", "79228162514264337593543950336", "0.00000000000000000000000000001", "1e-29", "1e29", "-1", "null", "\"1.2\"" })
                Assert.Equal(HttpStatusCode.BadRequest, (await Save(beforeVersion, invalid)).StatusCode);
            Assert.Equal(auditCount, await db.AdminAuditEvents.CountAsync(x => x.ObjectId == id.ToString()));
            Assert.Equal(historyCount, await db.AppointmentStatusEvents.CountAsync(x => x.AppointmentId == id));
            Assert.Equal(7922816251426433759354395033.5m, (await db.Set<RepairPlan>().AsNoTracking().SingleAsync(x => x.AppointmentId == id)).AgreedPrice);
        }
        Assert.Equal(beforeVersion, await Version(actor, id));
        var own = await (await customer.GetAsync($"/api/appointments/my/{id}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("7922816251426433759354395033.5", own.GetProperty("repairPlan").GetProperty("agreedPriceExact").GetString());
        var list = await (await customer.GetAsync("/api/appointments/my")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(list.EnumerateArray(), x => x.GetProperty("id").GetGuid() == id && x.GetProperty("repairPlan").GetProperty("agreedPriceExact").GetString() == "7922816251426433759354395033.5");
        var board = await (await actor.GetAsync("/api/admin/appointments?status=AwaitingInspection&search=" + Uri.EscapeDataString(owner.Phone))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(board.GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetGuid() == id && x.GetProperty("repairPlan").GetProperty("agreedPriceExact").GetString() == "7922816251426433759354395033.5");
    }

    [PostgresFact]
    public async Task ExactPriceConcurrentCorrectionsHaveOneWinnerAndOneConflict()
    {
        var owner = await User("User"); var admin = await User("Admin"); var otherAdmin = await User("Admin"); var id = await Add(owner.Id);
        using var first = await Login(admin.Phone); using var second = await Login(otherAdmin.Phone);
        Assert.Equal(HttpStatusCode.NoContent, (await first.PutAsJsonAsync(Url(id) + "/repair-plan", Plan(await Version(first, id), price: 123.45m))).StatusCode);
        var version = await Version(first, id);
        var results = await Task.WhenAll(first.PutAsJsonAsync(Url(id) + "/repair-plan", Plan(version, price: 9007199254740993.01m)),
            second.PutAsJsonAsync(Url(id) + "/repair-plan", Plan(version, price: 9007199254740993.02m)));
        Assert.Equal(1, results.Count(x => x.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(1, results.Count(x => x.StatusCode == HttpStatusCode.Conflict));
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var price = (await db.Set<RepairPlan>().SingleAsync(x => x.AppointmentId == id)).AgreedPrice;
        Assert.Equal(results[0].StatusCode == HttpStatusCode.NoContent ? 9007199254740993.01m : 9007199254740993.02m, price);
        var audits = await db.AdminAuditEvents.Where(x => x.ObjectId == id.ToString() && x.Action == "RepairPlanChanged").OrderBy(x => x.OccurredAtUtc).ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.Equal(admin.Id, audits[0].ActorId);
        Assert.Equal(results[0].StatusCode == HttpStatusCode.NoContent ? admin.Id : otherAdmin.Id, audits[1].ActorId);
    }

    [PostgresFact]
    public async Task RealApiPersistsGuardedLifecycleAuditAndPaymentHandover()
    {
        var owner = await User("User"); var admin = await User("Admin"); var id = await Add(owner.Id);
        using var actor = await Login(admin.Phone); using var customer = await Login(owner.Phone);
        var version = await Version(actor, id);
        Assert.Equal(HttpStatusCode.Conflict, (await Transition(actor, id, version, "Cancelled")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Transition(actor, id, version, "Scheduled", inspection: true)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await actor.PutAsJsonAsync(Url(id) + "/repair-plan", Plan(version, false))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await actor.PutAsJsonAsync(Url(id) + "/repair-plan", Plan(version, price: -1m))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await actor.PutAsJsonAsync(Url(id) + "/repair-plan", new { version, price = 5, plannedStartUtc = PlanStart, estimatedFinishUtc = PlanStart.AddDays(-1), inspectionPerformed = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await actor.PutAsJsonAsync(Url(id) + "/repair-plan", new { version, price = 5, plannedStartUtc = PlanStart, estimatedFinishUtc = PlanStart, inspectionPerformed = true, status = "Completed" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await actor.PutAsJsonAsync(Url(id) + "/repair-plan", Plan(version))).StatusCode);
        var details = await Details(actor, id);
        Assert.Equal("AwaitingInspection", details.GetProperty("status").GetString());
        Assert.Equal(100m, details.GetProperty("repairPlan").GetProperty("agreedPrice").GetDecimal());
        var newVersion = details.GetProperty("version").GetUInt32(); Assert.NotEqual(version, newVersion);
        Assert.Equal(HttpStatusCode.Conflict, (await Transition(actor, id, version, "Scheduled", inspection: true)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Transition(actor, id, newVersion, "Scheduled")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Transition(actor, id, newVersion, "Scheduled", inspection: true)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Transition(actor, id, await Version(actor, id), "Completed", payment: true, handover: true)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Transition(actor, id, await Version(actor, id), "InProgress")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Transition(actor, id, await Version(actor, id), "AwaitingPayment")).StatusCode);
        version = await Version(actor, id);
        foreach (var (payment, handover) in new[] { (false, false), (true, false), (false, true) })
            Assert.Equal(HttpStatusCode.Conflict, (await Transition(actor, id, version, "Completed", payment: payment, handover: handover)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await actor.PostAsJsonAsync(Url(id) + "/transition", new { version, target = "Completed", paymentReceived = true, vehicleHandedOver = true, paidAtUtc = PlanStart })).StatusCode);
        var before = DateTimeOffset.UtcNow;
        Assert.Equal(HttpStatusCode.NoContent, (await Transition(actor, id, version, "Completed", payment: true, handover: true)).StatusCode);
        var after = DateTimeOffset.UtcNow;
        Assert.Equal(HttpStatusCode.Conflict, (await Cancel(actor, id, await Version(actor, id))).StatusCode);
        var customerDetails = await Details(customer, id);
        Assert.Equal("Completed", customerDetails.GetProperty("status").GetString());
        Assert.Equal(100m, customerDetails.GetProperty("repairPlan").GetProperty("agreedPrice").GetDecimal());
        Assert.False(customerDetails.TryGetProperty("customerPhone", out _));
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var events = await db.AppointmentStatusEvents.Where(e => e.AppointmentId == id).ToListAsync();
        Assert.Equal(5, events.Count);
        Assert.Equal(new[] { AppointmentStatus.Scheduled, AppointmentStatus.InProgress, AppointmentStatus.AwaitingPayment, AppointmentStatus.Completed },
            events.Where(e => e.FromStatus != null).OrderBy(e => e.ChangedAtUtc).Select(e => e.ToStatus));
        var notifications = await db.Notifications.AsNoTracking().Where(n => n.AppointmentId == id).ToListAsync();
        foreach (var type in new[] { "RepairPlanChanged", "AppointmentScheduled", "AppointmentInProgress", "AppointmentAwaitingPayment", "AppointmentCompleted" })
            Assert.Single(notifications, n => n.RecipientId == owner.Id && n.Type == type);
        Assert.Single(notifications, n => n.RecipientId == admin.Id && n.Type == "AdminReadyForPayment");
        Assert.Single(notifications, n => n.RecipientId == admin.Id && n.Type == "AdminContactAgreement");
        Assert.All(notifications, n => Assert.Equal(n.RecipientId == owner.Id ? NotificationAudience.Personal : NotificationAudience.Administrator, n.Audience));
        Assert.Equal(notifications.Count, notifications.Select(n => n.EventKey).Distinct().Count());
        var completed = Assert.Single(events, e => e.ToStatus == AppointmentStatus.Completed);
        Assert.Equal(admin.Id, completed.ActorId);
        var confirmation = await db.PaymentHandoverConfirmations.SingleAsync(x => x.AppointmentId == id);
        Assert.Equal(admin.Id, confirmation.ActorId);
        Assert.Equal(completed.ChangedAtUtc, confirmation.PaidAtUtc);
        Assert.Equal(completed.ChangedAtUtc, confirmation.HandedOverAtUtc);
        Assert.InRange(confirmation.PaidAtUtc, before.AddSeconds(-1), after.AddSeconds(1));
        Assert.Equal(TimeSpan.Zero, confirmation.PaidAtUtc.Offset);
        var audit = await db.AdminAuditEvents.Where(e => e.ObjectId == id.ToString()).ToListAsync();
        Assert.Equal(5, audit.Count);
        Assert.Equal(4, audit.Count(e => e.Action == "AppointmentStatusChanged"));
        Assert.Single(audit, e => e.Action == "RepairPlanChanged");
        Assert.All(audit, e => Assert.Equal(admin.Id, e.ActorId));
        foreach (var statusEvent in events.Where(e => e.FromStatus != null))
        {
            var matching = audit.Where(e => e.Action == "AppointmentStatusChanged").Where(e =>
            {
                using var payload = JsonDocument.Parse(e.Details);
                return payload.RootElement.GetProperty("from").GetString() == statusEvent.FromStatus!.Value.ToString()
                    && payload.RootElement.GetProperty("to").GetString() == statusEvent.ToStatus.ToString();
            });
            var auditEvent = Assert.Single(matching);
            Assert.Equal(statusEvent.ActorId, auditEvent.ActorId);
            Assert.Equal("Appointment", auditEvent.ObjectType);
            using var detailsPayload = JsonDocument.Parse(auditEvent.Details);
            Assert.Equal(3, detailsPayload.RootElement.EnumerateObject().Count());
            Assert.Equal(statusEvent.Id, detailsPayload.RootElement.GetProperty("statusEventId").GetGuid());
        }
    }

    [PostgresFact]
    public async Task NotificationFailureRollsBackOperatorCancellationAndAudit()
    {
        var owner = await User("User"); var admin = await User("Admin"); var id = await Add(owner.Id);
        using var actor = await Login(admin.Phone); var version = await Version(actor, id);
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION notification_operator_reject() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW."Type" = 'AppointmentCancelled' THEN RAISE EXCEPTION 'test failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER notification_operator_reject_trigger BEFORE INSERT ON "Notifications" FOR EACH ROW EXECUTE FUNCTION notification_operator_reject();
            """);
        try
        {
            Assert.Equal(HttpStatusCode.InternalServerError, (await Cancel(actor, id, version)).StatusCode);
            Assert.Equal(AppointmentStatus.AwaitingInspection, (await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == id)).Status);
            Assert.Single(await db.AppointmentStatusEvents.Where(e => e.AppointmentId == id).ToListAsync());
            Assert.Empty(await db.AdminAuditEvents.Where(e => e.ObjectId == id.ToString()).ToListAsync());
            Assert.Empty(await db.Notifications.Where(n => n.AppointmentId == id).ToListAsync());
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER notification_operator_reject_trigger ON \"Notifications\"; DROP FUNCTION notification_operator_reject();"); }
    }

    [PostgresFact]
    public async Task PlanCorrectionPersistsWithoutTransitionAndUsesVersion()
    {
        var owner = await User("User"); var admin = await User("Admin"); var id = await Add(owner.Id, AppointmentStatus.Scheduled);
        using var actor = await Login(admin.Phone); using var customer = await Login(owner.Phone);
        var version = await Version(actor, id);
        Assert.Equal(HttpStatusCode.NoContent, (await actor.PutAsJsonAsync(Url(id) + "/repair-plan", Plan(version))).StatusCode);
        Assert.Equal(version, await Version(actor, id));
        Assert.Equal(HttpStatusCode.NoContent, (await actor.PutAsJsonAsync(Url(id) + "/repair-plan", Plan(version, price: 235.50m))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await actor.PutAsJsonAsync(Url(id) + "/repair-plan", Plan(version, price: 300m))).StatusCode);
        var details = await Details(actor, id);
        Assert.Equal("Scheduled", details.GetProperty("status").GetString());
        Assert.Equal(235.50m, details.GetProperty("repairPlan").GetProperty("agreedPrice").GetDecimal());
        Assert.Equal(235.50m, (await Details(customer, id)).GetProperty("repairPlan").GetProperty("agreedPrice").GetDecimal());
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Single(await db.AppointmentStatusEvents.Where(e => e.AppointmentId == id).ToListAsync());
        Assert.Single(await db.AdminAuditEvents.Where(e => e.ObjectId == id.ToString() && e.Action == "RepairPlanChanged").ToListAsync());
        Assert.Single(await db.Notifications.Where(n => n.AppointmentId == id && n.RecipientId == owner.Id && n.Type == "RepairPlanChanged").ToListAsync());
        Assert.Single(await db.Notifications.Where(n => n.AppointmentId == id && n.RecipientId == admin.Id && n.Type == "AdminContactPlanChanged").ToListAsync());
    }

    [PostgresFact]
    public async Task DateOnlyPlanChangeTargetsOwnerAndAdministratorsAndContactDoesNotReadNotifications()
    {
        var owner = await User("Admin"); var actor = await User("Admin"); var super = await User("SuperAdmin");
        var outsider = await User("User"); var blocked = await User("Admin");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var blockedUser = await db.Users.SingleAsync(u => u.Id == blocked.Id);
            blockedUser.IsBlocked = true;
            await db.SaveChangesAsync();
        }
        var id = await Add(owner.Id, AppointmentStatus.Scheduled);
        using var operatorClient = await Login(actor.Phone); using var ownerClient = await Login(owner.Phone);
        using var outsiderClient = await Login(outsider.Phone);
        var version = await Version(operatorClient, id);
        Assert.Equal(HttpStatusCode.NoContent, (await operatorClient.PutAsJsonAsync(Url(id) + "/repair-plan", Plan(version))).StatusCode);
        var revised = new { version, price = 100m, plannedStartUtc = PlanStart, estimatedFinishUtc = PlanStart.AddDays(2), inspectionPerformed = true };
        Assert.Equal(HttpStatusCode.NoContent, (await operatorClient.PutAsJsonAsync(Url(id) + "/repair-plan", revised)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await operatorClient.PutAsJsonAsync(Url(id) + "/repair-plan", revised)).StatusCode);
        await using var verify = factory.Services.CreateAsyncScope();
        var dbContext = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var notifications = await dbContext.Notifications.AsNoTracking().Where(n => n.AppointmentId == id).ToListAsync();
        var personal = Assert.Single(notifications, n => n.RecipientId == owner.Id && n.Type == "RepairPlanChanged");
        Assert.Single(notifications, n => n.RecipientId == owner.Id && n.Type == "AdminContactPlanChanged");
        Assert.Single(notifications, n => n.RecipientId == actor.Id && n.Type == "AdminContactPlanChanged");
        Assert.Single(notifications, n => n.RecipientId == super.Id && n.Type == "AdminContactPlanChanged");
        Assert.DoesNotContain(notifications, n => n.RecipientId == outsider.Id || n.RecipientId == blocked.Id);
        Assert.Single(notifications, n => n.Type == "RepairPlanChanged");
        Assert.Equal(notifications.Count, notifications.Select(n => n.EventKey).Distinct().Count());
        var ownerHistory = (await ownerClient.GetFromJsonAsync<JsonElement[]>("/api/notifications"))!;
        Assert.Equal($"/account/appointments/{id}", Assert.Single(ownerHistory, n => n.GetProperty("id").GetGuid() == personal.Id).GetProperty("link").GetString());
        Assert.Contains(ownerHistory, n => n.GetProperty("type").GetString() == "AdminContactPlanChanged" && n.GetProperty("link").GetString() == $"/admin/appointments/{id}");
        Assert.DoesNotContain((await outsiderClient.GetFromJsonAsync<JsonElement[]>("/api/notifications"))!, n => n.GetProperty("id").GetGuid() == personal.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await outsiderClient.PatchAsync($"/api/notifications/{personal.Id}/read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await operatorClient.PostAsJsonAsync(Url(id) + "/manual-contact", new { reason = "Agreement" })).StatusCode);
        var afterContact = await dbContext.Notifications.AsNoTracking().Where(n => n.AppointmentId == id).ToListAsync();
        Assert.Equal(notifications.Select(n => n.Id).Order(), afterContact.Select(n => n.Id).Order());
        Assert.All(afterContact, n => Assert.Null(n.ReadAtUtc));
        Assert.Single(await dbContext.ManualContactEvents.Where(e => e.AppointmentId == id).ToListAsync());
    }

    [PostgresFact]
    public async Task AuthenticationRolesCsrfAndSeparateCancellationRulesAreEnforced()
    {
        var owner = await User("User"); var admin = await User("Admin"); var super = await User("SuperAdmin");
        var id = await Add(admin.Id, past: true);
        using var anonymous = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(Url(id) + "/repair-plan", Plan(1))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Transition(anonymous, id, 1, "InProgress")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Cancel(anonymous, id, 1)).StatusCode);
        using var customer = await Login(owner.Phone); using var actor = await Login(admin.Phone); using var supervisor = await Login(super.Phone);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PutAsJsonAsync(Url(id) + "/repair-plan", Plan(1))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Transition(customer, id, 1, "Scheduled", inspection: true)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Cancel(customer, id, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Cancel(actor, Guid.NewGuid(), 1)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await actor.PostAsync($"/api/appointments/{id}/cancel", null)).StatusCode);
        var version = await Version(actor, id);
        actor.DefaultRequestHeaders.Remove("RequestVerificationToken");
        Assert.Equal(HttpStatusCode.BadRequest, (await Cancel(actor, id, version)).StatusCode);
        await Token(actor);
        Assert.Equal(HttpStatusCode.Conflict, (await Cancel(actor, id, version, false)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Cancel(actor, id, version, reason: "  ")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Cancel(actor, id, version, reason: "  Operator reason  ")).StatusCode);
        foreach (var state in new[] { AppointmentStatus.Scheduled, AppointmentStatus.InProgress, AppointmentStatus.AwaitingPayment, AppointmentStatus.Completed, AppointmentStatus.Cancelled })
        {
            var other = await Add(owner.Id, state);
            var result = await Cancel(supervisor, other, await Version(supervisor, other));
            Assert.Equal(state is AppointmentStatus.Scheduled or AppointmentStatus.InProgress ? HttpStatusCode.NoContent : HttpStatusCode.Conflict, result.StatusCode);
        }
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Single(await db.Notifications.Where(n => n.AppointmentId == id && n.RecipientId == admin.Id && n.Type == "AppointmentCancelled").ToListAsync());
        var history = await db.AppointmentStatusEvents.SingleAsync(e => e.AppointmentId == id && e.ToStatus == AppointmentStatus.Cancelled);
        Assert.Equal(admin.Id, history.ActorId); Assert.Equal("Operator reason", history.Reason);
        var audit = await db.AdminAuditEvents.SingleAsync(e => e.ObjectId == id.ToString());
        Assert.DoesNotContain("Operator reason", audit.Details);
        using (var safe = JsonDocument.Parse(audit.Details)) Assert.Equal(history.Id, safe.RootElement.GetProperty("statusEventId").GetGuid());
        var auditPage = await supervisor.GetFromJsonAsync<JsonElement>($"/api/admin/audit-log?actorId={admin.Id}&action=AppointmentStatusChanged&pageSize=100");
        var entry = Assert.Single(auditPage.GetProperty("items").EnumerateArray(), e => e.GetProperty("objectId").GetString() == id.ToString());
        Assert.Equal(history.Id, entry.GetProperty("details").GetProperty("statusEventId").GetGuid());
        Assert.DoesNotContain("Operator reason", entry.ToString());
        var appointmentDetails = await Details(supervisor, id);
        Assert.Contains(appointmentDetails.GetProperty("history").EnumerateArray(), e => e.GetProperty("reason").ValueKind == JsonValueKind.String && e.GetProperty("reason").GetString() == "Operator reason");
    }

    [PostgresFact]
    public async Task ConcurrentOperatorsWithSameVersionProduceOnlyOneCommittedChange()
    {
        var owner = await User("User"); var admin = await User("Admin"); var otherAdmin = await User("Admin");
        var id = await Add(owner.Id);
        using var first = await Login(admin.Phone); using var second = await Login(otherAdmin.Phone);
        Assert.Equal(HttpStatusCode.NoContent, (await first.PutAsJsonAsync(Url(id) + "/repair-plan", Plan(await Version(first, id)))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Transition(first, id, await Version(first, id), "Scheduled", inspection: true)).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var beforeHistory = await db.AppointmentStatusEvents.AsNoTracking().Where(e => e.AppointmentId == id).Select(e => e.Id).ToListAsync();
        Assert.Equal(2, beforeHistory.Count);
        var beforeAudit = await db.AdminAuditEvents.AsNoTracking().Where(e => e.ObjectId == id.ToString()).Select(e => e.Id).ToListAsync();
        Assert.Equal(2, beforeAudit.Count);
        var version = await Version(first, id);
        var board = await first.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/admin/appointments?status=Scheduled&search={Uri.EscapeDataString(owner.Phone)}");
        var card = Assert.Single(board.GetProperty("items").EnumerateArray());
        Assert.Equal(id, card.GetProperty("id").GetGuid());
        Assert.Equal(version, card.GetProperty("version").GetUInt32());
        var results = await Task.WhenAll(Transition(first, id, version, "InProgress"), Cancel(second, id, version));
        Assert.Equal(new[] { HttpStatusCode.NoContent, HttpStatusCode.Conflict }.Order(), results.Select(r => r.StatusCode).Order());
        var appointment = await db.Appointments.SingleAsync(a => a.Id == id);
        var winningState = results[0].StatusCode == HttpStatusCode.NoContent ? AppointmentStatus.InProgress : AppointmentStatus.Cancelled;
        var winningActor = results[0].StatusCode == HttpStatusCode.NoContent ? admin.Id : otherAdmin.Id;
        Assert.Equal(winningState, appointment.Status);
        var history = await db.AppointmentStatusEvents.AsNoTracking().Where(e => e.AppointmentId == id).ToListAsync();
        Assert.Equal(3, history.Count);
        Assert.All(beforeHistory, eventId => Assert.Contains(history, e => e.Id == eventId));
        var winningEvent = Assert.Single(history, e => e.FromStatus == AppointmentStatus.Scheduled);
        Assert.Equal(winningState, winningEvent.ToStatus);
        Assert.Equal(winningActor, winningEvent.ActorId);
        var audit = await db.AdminAuditEvents.AsNoTracking().Where(e => e.ObjectId == id.ToString()).ToListAsync();
        Assert.Equal(3, audit.Count);
        Assert.All(beforeAudit, eventId => Assert.Contains(audit, e => e.Id == eventId));
        Assert.Equal(winningActor, Assert.Single(audit, e => !beforeAudit.Contains(e.Id)).ActorId);
        foreach (var result in results) result.Dispose();
    }

    [PostgresFact]
    public async Task AuditInsertFailureRollsBackPlanTransitionAndCompletion()
    {
        var owner = await User("User"); var admin = await User("Admin");
        using var actor = await Login(admin.Phone);
        var ids = new[] { await Add(owner.Id), await Add(owner.Id, AppointmentStatus.Scheduled), await Add(owner.Id, AppointmentStatus.AwaitingPayment), await Add(owner.Id, AppointmentStatus.InProgress) };
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_appointment_audit_test() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW."Action" IN ('AppointmentStatusChanged', 'RepairPlanChanged') THEN RAISE EXCEPTION 'simulated audit failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER reject_appointment_audit_test BEFORE INSERT ON "AdminAuditEvents" FOR EACH ROW EXECUTE FUNCTION reject_appointment_audit_test();
            """);
        try
        {
            Assert.Equal(HttpStatusCode.InternalServerError, (await actor.PutAsJsonAsync(Url(ids[0]) + "/repair-plan", Plan(await Version(actor, ids[0])))).StatusCode);
            Assert.Equal(HttpStatusCode.InternalServerError, (await Transition(actor, ids[1], await Version(actor, ids[1]), "InProgress")).StatusCode);
            Assert.Equal(HttpStatusCode.InternalServerError, (await Transition(actor, ids[2], await Version(actor, ids[2]), "Completed", payment: true, handover: true)).StatusCode);
            Assert.Equal(HttpStatusCode.InternalServerError, (await Cancel(actor, ids[3], await Version(actor, ids[3]))).StatusCode);
            foreach (var (id, state) in ids.Zip(new[] { AppointmentStatus.AwaitingInspection, AppointmentStatus.Scheduled, AppointmentStatus.AwaitingPayment, AppointmentStatus.InProgress }))
            {
                var item = await db.Appointments.AsNoTracking().Include(a => a.RepairPlan).SingleAsync(a => a.Id == id);
                Assert.Equal(state, item.Status);
                if (state == AppointmentStatus.AwaitingInspection) Assert.Null(item.RepairPlan);
                Assert.Single(await db.AppointmentStatusEvents.Where(e => e.AppointmentId == id).ToListAsync());
                Assert.Empty(await db.AdminAuditEvents.Where(e => e.ObjectId == id.ToString()).ToListAsync());
                Assert.Empty(await db.PaymentHandoverConfirmations.Where(e => e.AppointmentId == id).ToListAsync());
            }
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_appointment_audit_test ON \"AdminAuditEvents\"; DROP FUNCTION reject_appointment_audit_test();"); }
    }
}
