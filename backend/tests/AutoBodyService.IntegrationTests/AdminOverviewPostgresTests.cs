using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Application.InspectionSchedule;
using AutoBodyService.Application.Appointments;
using AutoBodyService.Infrastructure.Audit;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.InspectionSchedule;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AutoBodyService.IntegrationTests;

[Collection("PostgresIdentity")]
public sealed class AdminOverviewPostgresTests(PostgresIdentityFixture fixture) : IDisposable
{
    private static readonly DateTimeOffset Now = new(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string Password = "Example-password-123!";
    private readonly WebApplicationFactory<Program> factory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
    {
        services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(new FixedClock(Now));
    }));
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    public void Dispose() => factory.Dispose();
    private HttpClient Client() => factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
    private async Task<(string Id, string Phone)> User(string role, string name)
    {
        await using var scope = factory.Services.CreateAsyncScope(); var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var phone = "+380" + RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000);
        var user = new ApplicationUser { Name = name, UserName = phone, PhoneNumber = phone, PhoneNormalized = phone };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded); Assert.True((await users.AddToRoleAsync(user, role)).Succeeded); return (user.Id, phone);
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
    private async Task<Guid> Add(string owner, AppointmentStatus status, DateTimeOffset slot, DateTimeOffset created, string? actorId = null)
    {
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var item = new Appointment(Guid.NewGuid(), owner, slot, "1HGCM82633A123456", "Make", "Model", "Private issue", created);
        if (status != AppointmentStatus.AwaitingInspection)
        {
            item.Schedule(new RepairPlan(item.Id, 777m, slot, slot.AddDays(1), created), true);
            if (status is AppointmentStatus.InProgress or AppointmentStatus.AwaitingPayment or AppointmentStatus.Completed) item.StartRepair();
            if (status is AppointmentStatus.AwaitingPayment or AppointmentStatus.Completed) item.FinishRepair();
            if (status == AppointmentStatus.Completed) item.Complete(true, true);
            if (status == AppointmentStatus.Cancelled) item.CancelByAdmin(true, "reason");
        }
        db.Appointments.Add(item);
        if (status == AppointmentStatus.Scheduled) db.AppointmentStatusEvents.Add(AppointmentStatusEvent.ForTransition(item.Id, AppointmentStatus.AwaitingInspection, status, actorId!, created.AddSeconds(1)));
        if (status == AppointmentStatus.AwaitingPayment) db.AppointmentStatusEvents.Add(AppointmentStatusEvent.ForTransition(item.Id, AppointmentStatus.InProgress, status, actorId!, created.AddSeconds(1)));
        if (status == AppointmentStatus.Cancelled) db.AppointmentStatusEvents.Add(AppointmentStatusEvent.ForTransition(item.Id, AppointmentStatus.Scheduled, status, actorId!, Now.AddMinutes(-1), "reason"));
        if (status == AppointmentStatus.Completed) db.PaymentHandoverConfirmations.Add(new PaymentHandoverConfirmation(item.Id, actorId!, Now.AddHours(-1)));
        await db.SaveChangesAsync(); return item.Id;
    }
    private static async Task<JsonElement> Read(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [PostgresFact]
    public async Task ListEnforcesRolesFiltersStablePagingSortingAndKyivTime()
    {
        var tag = "Search" + Guid.NewGuid().ToString("N")[..12]; var owner = await User("User", tag); var admin = await User("Admin", "Admin"); var super = await User("SuperAdmin", "Super");
        var all = Enum.GetValues<AppointmentStatus>(); var ids = new List<Guid>();
        var salt = RandomNumberGenerator.GetInt32(1, 1000);
        foreach (var status in all) ids.Add(await Add(owner.Id, status, Now.AddDays((int)status + 2).AddSeconds(salt), Now.AddMinutes(-(int)status - 1), admin.Id));
        using var anonymous = Client(); using var user = await Login(owner.Phone); using var actor = await Login(admin.Phone); using var supervisor = await Login(super.Phone);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/appointments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/admin/appointments")).StatusCode);
        var query = "?search=" + tag + "&pageSize=2&sort=inspection";
        var first = await Read(await actor.GetAsync("/api/admin/appointments" + query + "&page=1"));
        var second = await Read(await actor.GetAsync("/api/admin/appointments" + query + "&page=2"));
        Assert.Equal(6, first.GetProperty("total").GetInt32()); Assert.Equal(2, first.GetProperty("items").GetArrayLength());
        Assert.Equal(ids[0], first.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal(ids[2], second.GetProperty("items")[0].GetProperty("id").GetGuid());
        var reversed = await Read(await supervisor.GetAsync("/api/admin/appointments" + query + "&direction=desc"));
        Assert.Equal(ids[^1], reversed.GetProperty("items")[0].GetProperty("id").GetGuid());
        var byCreated = await Read(await actor.GetAsync("/api/admin/appointments?search=" + tag + "&sort=created&direction=asc"));
        Assert.Equal(ids[^1], byCreated.GetProperty("items")[0].GetProperty("id").GetGuid());
        var byCustomer = await Read(await actor.GetAsync("/api/admin/appointments?search=" + tag + "&sort=customer"));
        Assert.Equal(ids.Select(id => id.ToString()).Order(StringComparer.Ordinal), byCustomer.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid().ToString()));
        var byPhone = await Read(await actor.GetAsync("/api/admin/appointments?search=" + Uri.EscapeDataString(owner.Phone)));
        Assert.Equal(6, byPhone.GetProperty("total").GetInt32());
        var local = first.GetProperty("items")[0].GetProperty("inspectionStartLocal").GetDateTimeOffset();
        Assert.Equal(TimeSpan.FromHours(2), local.Offset);
        Assert.Equal(first.GetProperty("items")[0].GetProperty("inspectionStartUtc").GetDateTimeOffset(), local);
        Assert.False(first.GetProperty("items")[0].TryGetProperty("passwordHash", out _));
        foreach (var status in all)
        {
            var result = await Read(await actor.GetAsync("/api/admin/appointments?search=" + tag + "&status=" + status));
            Assert.Equal(1, result.GetProperty("total").GetInt32());
            var item = Assert.Single(result.GetProperty("items").EnumerateArray());
            Assert.Equal(status.ToString(), item.GetProperty("status").GetString());
            if (status == AppointmentStatus.AwaitingInspection) Assert.Equal(JsonValueKind.Null, item.GetProperty("repairPlan").ValueKind);
            else Assert.Equal(777m, item.GetProperty("repairPlan").GetProperty("agreedPrice").GetDecimal());
        }
        var wildcard = await Read(await actor.GetAsync("/api/admin/appointments?search=" + tag + "%25"));
        Assert.Equal(0, wildcard.GetProperty("total").GetInt32());
        var exactDay = DateOnly.FromDateTime(first.GetProperty("items")[0].GetProperty("inspectionStartLocal").GetDateTimeOffset().DateTime).ToString("yyyy-MM-dd");
        var day = await Read(await actor.GetAsync($"/api/admin/appointments?search={tag}&from={exactDay}&to={exactDay}"));
        Assert.Equal(1, day.GetProperty("total").GetInt32());
        var summerA = new DateTimeOffset(2099, 7, 1, 20, 30, 0, TimeSpan.Zero).AddSeconds(salt);
        var summerB = new DateTimeOffset(2099, 7, 1, 21, 30, 0, TimeSpan.Zero).AddSeconds(salt);
        await Add(owner.Id, AppointmentStatus.AwaitingInspection, summerA, Now.AddDays(-2));
        var afterMidnight = await Add(owner.Id, AppointmentStatus.AwaitingInspection, summerB, Now.AddDays(-2));
        var summerDay = await Read(await actor.GetAsync($"/api/admin/appointments?search={tag}&from=2099-07-02&to=2099-07-02"));
        var summerItem = Assert.Single(summerDay.GetProperty("items").EnumerateArray());
        Assert.Equal(afterMidnight, summerItem.GetProperty("id").GetGuid());
        Assert.Equal(TimeSpan.FromHours(3), summerItem.GetProperty("inspectionStartLocal").GetDateTimeOffset().Offset);
        foreach (var invalid in new[] { "?page=0", "?pageSize=101", "?sort=unknown", "?status=99", "?from=2099-02-01&to=2099-01-01" })
            Assert.Equal(HttpStatusCode.BadRequest, (await actor.GetAsync("/api/admin/appointments" + invalid)).StatusCode);
    }

    [PostgresFact]
    public async Task DashboardCountsAndManualTasksUseRealHistoryWithoutFinancialData()
    {
        var owner = await User("User", "Dashboard User"); var admin = await User("Admin", "Contact Actor");
        var id = await Add(owner.Id, AppointmentStatus.AwaitingInspection, Now.AddDays(1).AddHours(5).AddMinutes(7).AddSeconds(13), Now.AddMinutes(-5));
        var cancelled = await Add(owner.Id, AppointmentStatus.Cancelled, Now.AddDays(2).AddHours(10).AddSeconds(1), Now.AddMinutes(-4), admin.Id);
        var agreed = await Add(owner.Id, AppointmentStatus.Scheduled, Now.AddDays(3).AddHours(11).AddSeconds(2), Now.AddMinutes(-3), admin.Id);
        var ready = await Add(owner.Id, AppointmentStatus.AwaitingPayment, Now.AddDays(4).AddHours(12).AddSeconds(3), Now.AddMinutes(-2), admin.Id);
        using var actor = await Login(admin.Phone); using var user = await Login(owner.Phone); using var anonymous = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/admin/dashboard")).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await scope.ServiceProvider.GetRequiredService<IAdminOverviewQueries>().GetDashboardAsync(default);
        var dashboard = await Read(await actor.GetAsync("/api/admin/dashboard"));
        var counts = dashboard.GetProperty("counts");
        foreach (var (name, status) in new[] { ("awaitingInspection", AppointmentStatus.AwaitingInspection), ("scheduled", AppointmentStatus.Scheduled), ("inProgress", AppointmentStatus.InProgress), ("awaitingPayment", AppointmentStatus.AwaitingPayment) })
            Assert.Equal(await db.Appointments.CountAsync(a => a.Status == status), counts.GetProperty(name).GetInt32());
        Assert.Contains(dashboard.GetProperty("upcomingInspections").EnumerateArray(), x => x.GetProperty("id").GetGuid() == id);
        Assert.Contains(dashboard.GetProperty("newAppointments").EnumerateArray(), x => x.GetProperty("id").GetGuid() == id);
        Assert.Contains(dashboard.GetProperty("cancellations").EnumerateArray(), x => x.GetProperty("id").GetGuid() == cancelled);
        var tasks = dashboard.GetProperty("contactTasks").EnumerateArray().Where(x => x.GetProperty("appointmentId").GetGuid() == id).ToArray();
        Assert.Contains(tasks, x => x.GetProperty("reason").GetString() == "NewAppointment");
        Assert.Contains(tasks, x => x.GetProperty("reason").GetString() == "ScheduleConflictOrReschedule");
        Assert.Contains(dashboard.GetProperty("contactTasks").EnumerateArray(), x => x.GetProperty("appointmentId").GetGuid() == agreed && x.GetProperty("reason").GetString() == "Agreement");
        Assert.Contains(dashboard.GetProperty("contactTasks").EnumerateArray(), x => x.GetProperty("appointmentId").GetGuid() == ready && x.GetProperty("reason").GetString() == "ReadyForPayment");
        var raw = dashboard.GetRawText();
        foreach (var forbidden in new[] { "agreedPrice", "revenue", "expenses", "password", "customerPhone", "Private issue" })
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NoContent, (await actor.PostAsJsonAsync($"/api/admin/appointments/{id}/manual-contact", new { reason = "NewAppointment" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await actor.PostAsJsonAsync($"/api/admin/appointments/{id}/manual-contact", new { reason = "ScheduleConflictOrReschedule" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await actor.PostAsJsonAsync($"/api/admin/appointments/{agreed}/manual-contact", new { reason = "Agreement" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await actor.PostAsJsonAsync($"/api/admin/appointments/{ready}/manual-contact", new { reason = "ReadyForPayment" })).StatusCode);
        dashboard = await Read(await actor.GetAsync("/api/admin/dashboard"));
        Assert.DoesNotContain(dashboard.GetProperty("contactTasks").EnumerateArray(), x => x.GetProperty("appointmentId").GetGuid() == id);
        Assert.DoesNotContain(dashboard.GetProperty("contactTasks").EnumerateArray(), x => x.GetProperty("appointmentId").GetGuid() == agreed && x.GetProperty("reason").GetString() == "Agreement");
        Assert.DoesNotContain(dashboard.GetProperty("contactTasks").EnumerateArray(), x => x.GetProperty("appointmentId").GetGuid() == ready && x.GetProperty("reason").GetString() == "ReadyForPayment");
        var details = await Read(await actor.GetAsync($"/api/appointments/{id}"));
        Assert.Equal(owner.Phone, details.GetProperty("customerPhone").GetString());
        Assert.Equal("1HGCM82633A123456", details.GetProperty("vin").GetString());
        Assert.Equal("Private issue", details.GetProperty("problemDescription").GetString());
        var contacts = details.GetProperty("manualContacts").EnumerateArray().ToArray(); Assert.Equal(2, contacts.Length);
        Assert.All(contacts, c => { Assert.Equal("Contact Actor", c.GetProperty("actorName").GetString()); Assert.Equal(Now, c.GetProperty("recordedAtUtc").GetDateTimeOffset()); });
        Assert.Equal(JsonValueKind.Null, details.GetProperty("assignments").ValueKind);
        var own = await Read(await user.GetAsync($"/api/appointments/{id}"));
        Assert.False(own.TryGetProperty("manualContacts", out _)); Assert.False(own.TryGetProperty("customerPhone", out _));
        var audit = await db.AdminAuditEvents.AsNoTracking().Where(e => e.ObjectId == id.ToString() && e.Action == "ManualContactRecorded").ToListAsync();
        Assert.Equal(2, audit.Count); Assert.All(audit, e => Assert.Equal(admin.Id, e.ActorId));
        Assert.Equal(2, await db.ManualContactEvents.CountAsync(e => e.AppointmentId == id));

        // A subsequent real schedule change can make a conflict require contact again.
        var weekly = scope.ServiceProvider.GetRequiredService<IInspectionScheduleService>();
        var exceptions = scope.ServiceProvider.GetRequiredService<IInspectionExceptionsService>();
        var laterClock = new FixedClock(Now.AddMinutes(1));
        var calendar = new InspectionCalendarService(db, weekly, exceptions, new AdminAuditWriter(db, laterClock), laterClock);
        var current = await weekly.GetAsync(default);
        WeeklyInterval[] next = [new(DayOfWeek.Monday, new TimeOnly(1, 0), new TimeOnly(1, 30))];
        if (current.Intervals.SequenceEqual(next)) next = [new(DayOfWeek.Tuesday, new TimeOnly(1, 0), new TimeOnly(1, 30))];
        var preview = await calendar.ChangeWeeklyAsync(admin.Id, current.Version, next, true, null, default);
        db.ChangeTracker.Clear();
        var update = await calendar.ChangeWeeklyAsync(admin.Id, current.Version, next, false, preview.Preview!.ConfirmationToken, default);
        Assert.Equal(ScheduleUpdateResult.Success, update.Status);
        dashboard = await Read(await actor.GetAsync("/api/admin/dashboard"));
        Assert.Contains(dashboard.GetProperty("contactTasks").EnumerateArray(), x => x.GetProperty("appointmentId").GetGuid() == id && x.GetProperty("reason").GetString() == "ScheduleConflictOrReschedule");
    }

    [PostgresFact]
    public async Task ManualContactValidatesRolesReasonsCsrfAndRollsBackWhenAuditFails()
    {
        var owner = await User("User", "User"); var admin = await User("Admin", "Admin"); var super = await User("SuperAdmin", "Super");
        var id = await Add(owner.Id, AppointmentStatus.AwaitingInspection, Now.AddDays(5).AddSeconds(RandomNumberGenerator.GetInt32(1, 20000)), Now.AddMinutes(-1));
        using var actor = await Login(admin.Phone); using var customer = await Login(owner.Phone); using var supervisor = await Login(super.Phone); using var anonymous = Client();
        var url = $"/api/admin/appointments/{id}/manual-contact";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(url, new { reason = "Agreement" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsJsonAsync(url, new { reason = "Agreement" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await actor.PostAsJsonAsync($"/api/admin/appointments/{Guid.NewGuid()}/manual-contact", new { reason = "Agreement" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await actor.PostAsJsonAsync(url, new { reason = 99 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await actor.PostAsJsonAsync(url, new { reason = "Agreement", actorId = owner.Id })).StatusCode);
        actor.DefaultRequestHeaders.Remove("RequestVerificationToken");
        Assert.Equal(HttpStatusCode.BadRequest, (await actor.PostAsJsonAsync(url, new { reason = "Agreement" })).StatusCode);
        await Token(actor);
        Assert.Equal(HttpStatusCode.NoContent, (await supervisor.PostAsJsonAsync(url, new { reason = "ReadyForPayment" })).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_manual_contact_audit_test() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW."Action" = 'ManualContactRecorded' THEN RAISE EXCEPTION 'simulated audit failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER reject_manual_contact_audit_test BEFORE INSERT ON "AdminAuditEvents" FOR EACH ROW EXECUTE FUNCTION reject_manual_contact_audit_test();
            """);
        try
        {
            Assert.Equal(HttpStatusCode.InternalServerError, (await actor.PostAsJsonAsync(url, new { reason = "Agreement" })).StatusCode);
            var events = await db.ManualContactEvents.Where(e => e.AppointmentId == id).ToListAsync();
            var existing = Assert.Single(events); Assert.Equal(ManualContactReason.ReadyForPayment, existing.Reason); Assert.Equal(super.Id, existing.ActorId);
            Assert.Single(await db.AdminAuditEvents.Where(e => e.ObjectId == id.ToString() && e.Action == "ManualContactRecorded").ToListAsync());
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_manual_contact_audit_test ON \"AdminAuditEvents\"; DROP FUNCTION reject_manual_contact_audit_test();"); }
    }

    [PostgresFact]
    public async Task TerminalOrdersRejectContactWithoutChangingHistoryAndStayVisibleInCalendar()
    {
        var owner = await User("User", "Owner"); var admin = await User("Admin", "Operator");
        var firstSlot = Now.AddDays(300).AddSeconds(37);
        var completed = await Add(owner.Id, AppointmentStatus.Completed, firstSlot, Now.AddDays(-1), admin.Id);
        var cancelled = await Add(owner.Id, AppointmentStatus.Cancelled, firstSlot.AddSeconds(1), Now.AddDays(-1), admin.Id);
        using var actor = await Login(admin.Phone);
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        foreach (var id in new[] { completed, cancelled })
            db.ManualContactEvents.Add(new ManualContactEvent(id, ManualContactReason.Agreement, admin.Id, Now.AddHours(-1)));
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var before = new Dictionary<Guid, JsonElement>();
        foreach (var id in new[] { completed, cancelled }) before[id] = await Read(await actor.GetAsync($"/api/appointments/{id}"));
        var conflictRows = await scope.ServiceProvider.GetRequiredService<IInspectionCalendarService>().GetCurrentConflictsAsync(default);
        Assert.Contains(conflictRows, item => item.Id == completed);
        var dashboard = await Read(await actor.GetAsync("/api/admin/dashboard"));
        Assert.DoesNotContain(dashboard.GetProperty("contactTasks").EnumerateArray(), task => task.GetProperty("appointmentId").GetGuid() == completed || task.GetProperty("appointmentId").GetGuid() == cancelled);
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(firstSlot, TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv")).DateTime);
        var day = await Read(await actor.GetAsync($"/api/admin/inspection-calendar?date={date:yyyy-MM-dd}"));
        Assert.Contains(day.EnumerateArray(), item => item.GetProperty("id").GetGuid() == completed);
        Assert.Contains(day.EnumerateArray(), item => item.GetProperty("id").GetGuid() == cancelled);
        foreach (var id in new[] { completed, cancelled })
        {
            using var response = await actor.PostAsJsonAsync($"/api/admin/appointments/{id}/manual-contact", new { reason = "ScheduleConflictOrReschedule" });
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(409, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetInt32());
            var after = await Read(await actor.GetAsync($"/api/appointments/{id}"));
            Assert.Equal(before[id].GetProperty("status").GetString(), after.GetProperty("status").GetString());
            Assert.Equal(before[id].GetProperty("version").GetUInt32(), after.GetProperty("version").GetUInt32());
            Assert.Equal(before[id].GetProperty("history").GetArrayLength(), after.GetProperty("history").GetArrayLength());
            var contact = Assert.Single(after.GetProperty("manualContacts").EnumerateArray());
            Assert.Equal("Agreement", contact.GetProperty("reason").GetString());
            Assert.Equal("Operator", contact.GetProperty("actorName").GetString());
            Assert.Single(await db.ManualContactEvents.AsNoTracking().Where(e => e.AppointmentId == id).ToListAsync());
            Assert.Empty(await db.AdminAuditEvents.AsNoTracking().Where(e => e.ObjectId == id.ToString() && e.Action == "ManualContactRecorded").ToListAsync());
        }
    }

    [PostgresFact]
    public async Task ContactAndTerminalCommandsSerializeInBothOrders()
    {
        var owner = await User("User", "Owner"); var admin = await User("Admin", "Operator");
        using var actor = await Login(admin.Phone); using var other = await Login(admin.Phone);
        var cases = new[] { (AppointmentStatus.AwaitingPayment, "Completed"), (AppointmentStatus.Scheduled, "Cancelled") };
        var index = 0;
        foreach (var (initial, terminal) in cases)
        foreach (var contactFirst in new[] { false, true })
        {
            var id = await Add(owner.Id, initial, Now.AddDays(320 + index++).AddSeconds(37), Now.AddDays(-1), admin.Id);
            var version = (await Read(await actor.GetAsync($"/api/appointments/{id}"))).GetProperty("version").GetUInt32();
            await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await using var gate = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Appointments\" WHERE \"Id\" = {id} FOR UPDATE");
            Task<HttpResponseMessage> Contact() => other.PostAsJsonAsync($"/api/admin/appointments/{id}/manual-contact", new { reason = "Agreement" });
            Task<HttpResponseMessage> Finish() => terminal == "Completed"
                ? actor.PostAsJsonAsync($"/api/admin/appointments/{id}/transition", new { version, target = terminal, paymentReceived = true, vehicleHandedOver = true })
                : actor.PostAsJsonAsync($"/api/admin/appointments/{id}/cancel", new { version, confirmed = true, reason = "test" });
            var first = contactFirst ? Contact() : Finish();
            await WaitForAppointmentLockWaiters(db, first, 1);
            var second = contactFirst ? Finish() : Contact();
            await WaitForAppointmentLockWaiters(db, second, 2);
            await gate.CommitAsync();
            using var firstResult = await first; using var secondResult = await second;
            Assert.Equal(HttpStatusCode.NoContent, firstResult.StatusCode);
            Assert.Equal(contactFirst ? HttpStatusCode.NoContent : HttpStatusCode.Conflict, secondResult.StatusCode);
            db.ChangeTracker.Clear();
            var terminalStatus = Enum.Parse<AppointmentStatus>(terminal);
            Assert.Equal(terminalStatus, (await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == id)).Status);
            Assert.Equal(contactFirst ? 1 : 0, await db.ManualContactEvents.CountAsync(e => e.AppointmentId == id));
            Assert.Equal(contactFirst ? 1 : 0, await db.AdminAuditEvents.CountAsync(e => e.ObjectId == id.ToString() && e.Action == "ManualContactRecorded"));
            Assert.Single(await db.AppointmentStatusEvents.Where(e => e.AppointmentId == id && e.ToStatus == terminalStatus).ToListAsync());
        }
    }

    private static async Task WaitForAppointmentLockWaiters(ApplicationDbContext db, Task request, int expected)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            Assert.False(request.IsCompleted, "The request completed before the appointment lock was released.");
            // PostgreSQL caches statistics within a transaction; refresh while the gate lock is held.
            await db.Database.ExecuteSqlRawAsync("SELECT pg_stat_clear_snapshot()");
            var waiting = await db.Database.SqlQueryRaw<int>("""
                SELECT count(*)::int AS "Value" FROM pg_stat_activity
                WHERE datname = current_database() AND wait_event_type = 'Lock'
                  AND query ILIKE '%FROM "Appointments"%FOR UPDATE%'
                """).SingleAsync();
            if (waiting >= expected) return;
            await Task.Delay(25);
        }
        var states = await db.Database.SqlQueryRaw<string>("""
            SELECT concat(coalesce(state, '-'), '/', coalesce(wait_event_type, '-'), '/', coalesce(wait_event, '-'), '/', left(query, 50)) AS "Value"
            FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid()
            """).ToListAsync();
        throw new TimeoutException($"Expected {expected} appointment lock waiters: {string.Join("; ", states)}");
    }

    [PostgresFact]
    public async Task AdminDetailsExposePaymentConfirmationButOwnerDoesNot()
    {
        var owner = await User("User", "Owner"); var admin = await User("Admin", "Auditor");
        var id = await Add(owner.Id, AppointmentStatus.Completed, Now.AddDays(7).AddSeconds(RandomNumberGenerator.GetInt32(1, 20000)), Now.AddDays(-2), admin.Id);
        using var actor = await Login(admin.Phone); using var customer = await Login(owner.Phone);
        var details = await Read(await actor.GetAsync($"/api/appointments/{id}"));
        var payment = details.GetProperty("paymentConfirmation");
        Assert.Equal("Auditor", payment.GetProperty("actorName").GetString());
        Assert.Equal(Now.AddHours(-1), payment.GetProperty("paidAtUtc").GetDateTimeOffset());
        Assert.Equal(Now.AddHours(-1), payment.GetProperty("handedOverAtUtc").GetDateTimeOffset());
        var own = await Read(await customer.GetAsync($"/api/appointments/{id}"));
        Assert.False(own.TryGetProperty("paymentConfirmation", out _));
    }
}
