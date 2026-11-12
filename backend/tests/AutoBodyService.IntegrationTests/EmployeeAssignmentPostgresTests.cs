using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Domain.Assignments;
using AutoBodyService.Domain.Employees;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AutoBodyService.IntegrationTests;

public sealed class EmployeeAssignmentDomainTests
{
    [Fact]
    public void AssignRequiresActiveEmployeeAndRepairInProgressAndReleasePreservesSnapshot()
    {
        var now = DateTimeOffset.UtcNow; var id = Guid.NewGuid();
        var appointment = new Appointment(id, "owner", now.AddDays(1), "1HGCM82633A123456", "Test", "Car", "Issue", now);
        var employee = new Employee(Guid.NewGuid(), "Initial", "Repair", true);
        Assert.Throws<InvalidOperationException>(() => EmployeeAssignment.Assign(appointment, employee, "admin", now));
        appointment.Schedule(new RepairPlan(id, 10m, now, now.AddDays(1), now), true); appointment.StartRepair();
        Assert.Throws<ArgumentException>(() => EmployeeAssignment.Assign(appointment, employee, "admin", now.ToOffset(TimeSpan.FromHours(1))));
        var assignment = EmployeeAssignment.Assign(appointment, employee, "admin", now);
        employee.Update("Changed", "Paint", false);
        Assert.Equal("Initial", assignment.NameSnapshot); Assert.Equal("Repair", assignment.SpecializationSnapshot);
        Assert.Throws<InvalidOperationException>(() => EmployeeAssignment.Assign(appointment, employee, "admin", now));
        Assert.Throws<ArgumentException>(() => assignment.Release(now.AddTicks(-1)));
        assignment.Release(now.AddMinutes(1));
        Assert.Equal(now.AddMinutes(1), assignment.ReleasedAtUtc);
        Assert.Throws<InvalidOperationException>(() => assignment.Release(now.AddMinutes(2)));
    }
}

[Collection("PostgresIdentity")]
public sealed class EmployeeAssignmentPostgresTests(PostgresIdentityFixture fixture) : IDisposable
{
    private readonly WebApplicationFactory<Program> factory = fixture.Factory.WithWebHostBuilder(_ => { });
    private const string Password = "Example-password-123!";
    private const string Path = "/api/admin/assignments";
    public void Dispose() => factory.Dispose();

    private async Task<(string Id, string Phone)> User(string role)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var phone = "+380" + RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000);
        var user = new ApplicationUser { Name = "Test actor", UserName = phone, PhoneNumber = phone, PhoneNormalized = phone };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded); Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
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
    private async Task<Guid> Appointment(string owner, bool inProgress = true)
    {
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = DateTimeOffset.UtcNow; var id = Guid.NewGuid();
        var slot = new DateTimeOffset(2097, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(RandomNumberGenerator.GetInt32(1, 10_000_000));
        var appointment = new Appointment(id, owner, slot, "1HGCM82633A123456", "Test", "Car", "Issue", now);
        if (inProgress) { appointment.Schedule(new RepairPlan(id, 100m, slot, slot.AddDays(1), now), true); appointment.StartRepair(); }
        db.Appointments.Add(appointment); await db.SaveChangesAsync(); return id;
    }
    private async Task<Guid> Employee(string name, bool active = true)
    {
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var employee = new Employee(Guid.NewGuid(), name, "Repair", active); db.Employees.Add(employee); await db.SaveChangesAsync(); return employee.Id;
    }
    private static Task<HttpResponseMessage> Assign(HttpClient client, Guid appointment, Guid employee) => client.PostAsJsonAsync(Path, new { appointmentId = appointment, employeeId = employee });
    private static Task<HttpResponseMessage> Release(HttpClient client, Guid id) => client.PatchAsync($"{Path}/{id}/release", null);
    private static async Task<JsonElement> Body(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();
    private static async Task<uint> Version(HttpClient client, Guid id) => (await client.GetFromJsonAsync<JsonElement>($"/api/appointments/{id}")).GetProperty("version").GetUInt32();
    private static Task<HttpResponseMessage> Exit(HttpClient client, Guid id, uint version, bool cancel) => cancel
        ? client.PostAsJsonAsync($"/api/admin/appointments/{id}/cancel", new { version, confirmed = true, reason = "Test" })
        : client.PostAsJsonAsync($"/api/admin/appointments/{id}/transition", new { version, target = "AwaitingPayment" });

    [PostgresFact]
    public async Task AssignmentHistoryReleaseReassignSnapshotsFiltersAndOwnerPrivacy()
    {
        var owner = await User("User"); var outsider = await User("User"); var admin = await User("Admin"); var super = await User("SuperAdmin");
        using var actor = await Login(admin.Phone); using var supervisor = await Login(super.Phone);
        using var customer = await Login(owner.Phone); using var other = await Login(outsider.Phone);
        var order = await Appointment(owner.Id); var otherOrder = await Appointment(owner.Id); var worker = await Employee("Original"); var second = await Employee("Second");
        using var first = await Assign(actor, order, worker); Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstBody = await Body(first); var firstId = firstBody.GetProperty("id").GetGuid();
        Assert.Equal(admin.Id, firstBody.GetProperty("assignedById").GetString());
        Assert.Equal(HttpStatusCode.Created, (await Assign(supervisor, order, second)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Assign(actor, order, worker)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Release(actor, firstId)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Release(actor, firstId)).StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var employee = await db.Employees.SingleAsync(e => e.Id == worker); employee.Update("Renamed", "Paint", true); await db.SaveChangesAsync();
        }
        using var repeat = await Assign(actor, order, worker); Assert.Equal(HttpStatusCode.Created, repeat.StatusCode);
        var repeatId = (await Body(repeat)).GetProperty("id").GetGuid(); Assert.NotEqual(firstId, repeatId);
        Assert.Equal(HttpStatusCode.Created, (await Assign(actor, otherOrder, worker)).StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var employee = await db.Employees.SingleAsync(e => e.Id == worker); employee.Update("Later", "Polish", false); await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Conflict, (await Assign(actor, order, worker)).StatusCode);
        var history = await actor.GetFromJsonAsync<JsonElement[]>($"{Path}?appointmentId={order}&employeeId={worker}");
        Assert.Equal(2, history!.Length);
        var old = Assert.Single(history, a => a.GetProperty("id").GetGuid() == firstId);
        var current = Assert.Single(history, a => a.GetProperty("id").GetGuid() == repeatId);
        Assert.Equal("Original", old.GetProperty("name").GetString()); Assert.Equal("Repair", old.GetProperty("specialization").GetString());
        Assert.Equal("Renamed", current.GetProperty("name").GetString()); Assert.Equal("Paint", current.GetProperty("specialization").GetString());
        Assert.NotEqual(JsonValueKind.Null, old.GetProperty("releasedAtUtc").ValueKind);
        Assert.Single((await actor.GetFromJsonAsync<JsonElement[]>($"{Path}?appointmentId={order}&employeeId={worker}&active=true"))!);
        Assert.Single((await actor.GetFromJsonAsync<JsonElement[]>($"{Path}?appointmentId={order}&employeeId={worker}&active=false"))!);
        Assert.Equal(3, (await actor.GetFromJsonAsync<JsonElement[]>($"{Path}?employeeId={worker}"))!.Length);
        var detail = await actor.GetFromJsonAsync<JsonElement>($"/api/appointments/{order}");
        Assert.Equal(3, detail.GetProperty("assignments").GetArrayLength());
        var adminHistory = detail.GetProperty("assignments").EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == firstId);
        Assert.Equal(worker, adminHistory.GetProperty("employeeId").GetGuid());
        Assert.Equal(admin.Id, adminHistory.GetProperty("assignedById").GetString());
        Assert.Equal("Test actor", adminHistory.GetProperty("assignedByName").GetString());
        Assert.Equal("Original", adminHistory.GetProperty("name").GetString());
        Assert.Equal(old.GetProperty("assignedAtUtc").GetDateTimeOffset(), adminHistory.GetProperty("assignedAtUtc").GetDateTimeOffset());
        Assert.Equal(old.GetProperty("releasedAtUtc").GetDateTimeOffset(), adminHistory.GetProperty("releasedAtUtc").GetDateTimeOffset());
        Assert.Equal(TimeSpan.Zero, adminHistory.GetProperty("assignedAtUtc").GetDateTimeOffset().Offset);
        var own = await customer.GetFromJsonAsync<JsonElement>($"/api/appointments/{order}");
        var participants = own.GetProperty("participants"); Assert.Equal(2, participants.GetArrayLength());
        var person = Assert.Single(participants.EnumerateArray(), p => p.GetProperty("name").GetString() == "Renamed");
        Assert.Equal("Paint", person.GetProperty("specialization").GetString()); Assert.Equal(2, person.EnumerateObject().Count());
        Assert.False(own.TryGetProperty("assignments", out _));
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/appointments/{order}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync($"{Path}/{firstId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Assign(customer, order, second)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Release(customer, repeatId)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Release(actor, repeatId)).StatusCode);
        var remaining = await actor.GetFromJsonAsync<JsonElement[]>($"{Path}?appointmentId={order}&active=true");
        Assert.Equal(second, Assert.Single(remaining!).GetProperty("employeeId").GetGuid());
    }

    [PostgresFact]
    public async Task ValidationAndRoleChecksRejectInvalidAssignmentRequests()
    {
        var owner = await User("User"); var admin = await User("Admin"); using var actor = await Login(admin.Phone); using var anonymous = Client();
        var order = await Appointment(owner.Id); var pending = await Appointment(owner.Id, false); var active = await Employee("Active"); var inactive = await Employee("Inactive", false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Assign(anonymous, order, active)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Assign(actor, pending, active)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Assign(actor, order, inactive)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Assign(actor, Guid.NewGuid(), active)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Assign(actor, order, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Release(actor, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Assign(actor, Guid.Empty, active)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await actor.PostAsJsonAsync(Path, new { appointmentId = order, employeeId = active, assignedById = admin.Id })).StatusCode);
        actor.DefaultRequestHeaders.Remove("RequestVerificationToken");
        Assert.Equal(HttpStatusCode.BadRequest, (await Assign(actor, order, active)).StatusCode);
    }

    [PostgresFact]
    public async Task ConcurrentDuplicateRequestsAndPostgresPartialUniqueIndexEnforceOneActivePair()
    {
        var owner = await User("User"); var admin = await User("Admin"); var other = await User("Admin");
        using var a = await Login(admin.Phone); using var b = await Login(other.Phone);
        var order = await Appointment(owner.Id); var worker = await Employee("Worker");
        var results = await Task.WhenAll(Assign(a, order, worker), Assign(b, order, worker));
        Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }.Order(), results.Select(x => x.StatusCode).Order());
        var id = (await Body(results.Single(x => x.StatusCode == HttpStatusCode.Created))).GetProperty("id").GetGuid();
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.EmployeeAssignments.AsNoTracking().SingleAsync(x => x.Id == id);
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "EmployeeAssignments" ("Id", "AppointmentId", "EmployeeId", "AssignedById", "AssignedAtUtc", "ReleasedAtUtc", "NameSnapshot", "SpecializationSnapshot")
            VALUES ({Guid.NewGuid()}, {order}, {worker}, {admin.Id}, {row.AssignedAtUtc}, {null}, {"Test"}, {"Repair"})
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal("IX_EmployeeAssignments_AppointmentId_EmployeeId", error.ConstraintName);
        Assert.Single(await db.EmployeeAssignments.Where(x => x.AppointmentId == order && x.EmployeeId == worker && x.ReleasedAtUtc == null).ToListAsync());
        foreach (var response in results) response.Dispose();
    }

    [PostgresFact]
    public async Task BothExitPathsCloseAllAssignmentsWithStatusAndAuditAndRejectStaleVersion()
    {
        var owner = await User("User"); var admin = await User("Admin"); using var actor = await Login(admin.Phone);
        foreach (var cancel in new[] { false, true })
        {
            var order = await Appointment(owner.Id); var one = await Employee("First"); var two = await Employee("Second");
            Assert.Equal(HttpStatusCode.Created, (await Assign(actor, order, one)).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await Assign(actor, order, two)).StatusCode);
            var version = await Version(actor, order);
            Assert.Equal(HttpStatusCode.NoContent, (await Exit(actor, order, version, cancel)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await Exit(actor, order, version, cancel)).StatusCode);
            await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var appointment = await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == order);
            var expected = cancel ? AppointmentStatus.Cancelled : AppointmentStatus.AwaitingPayment;
            Assert.Equal(expected, appointment.Status);
            var statusEvent = await db.AppointmentStatusEvents.SingleAsync(e => e.AppointmentId == order && e.ToStatus == expected);
            var assignments = await db.EmployeeAssignments.AsNoTracking().Where(a => a.AppointmentId == order).ToListAsync();
            Assert.Equal(2, assignments.Count); Assert.All(assignments, a => Assert.Equal(statusEvent.ChangedAtUtc, a.ReleasedAtUtc));
            Assert.Single(await db.AdminAuditEvents.Where(e => e.ObjectId == order.ToString() && e.Action == "AppointmentStatusChanged").ToListAsync());
        }
    }

    [PostgresFact]
    public async Task AuditFailureRollsBackBothExitPathsAndAssignmentClosure()
    {
        var owner = await User("User"); var admin = await User("Admin"); using var actor = await Login(admin.Phone);
        var orders = new[] { await Appointment(owner.Id), await Appointment(owner.Id) }; var worker = await Employee("Rollback");
        foreach (var id in orders) Assert.Equal(HttpStatusCode.Created, (await Assign(actor, id, worker)).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION assignment_reject_audit_test() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW."Action" = 'AppointmentStatusChanged' THEN RAISE EXCEPTION 'test failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER assignment_reject_audit_test BEFORE INSERT ON "AdminAuditEvents" FOR EACH ROW EXECUTE FUNCTION assignment_reject_audit_test();
            """);
        try
        {
            for (var i = 0; i < orders.Length; i++)
            {
                var id = orders[i]; var version = await Version(actor, id);
                Assert.Equal(HttpStatusCode.InternalServerError, (await Exit(actor, id, version, i == 1)).StatusCode);
                Assert.Equal(version, await Version(actor, id));
                Assert.Equal(AppointmentStatus.InProgress, (await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == id)).Status);
                Assert.Null((await db.EmployeeAssignments.AsNoTracking().SingleAsync(a => a.AppointmentId == id)).ReleasedAtUtc);
                Assert.Single(await db.AppointmentStatusEvents.AsNoTracking().Where(e => e.AppointmentId == id).ToListAsync());
                Assert.Empty(await db.AdminAuditEvents.AsNoTracking().Where(e => e.ObjectId == id.ToString()).ToListAsync());
            }
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER assignment_reject_audit_test ON \"AdminAuditEvents\"; DROP FUNCTION assignment_reject_audit_test();"); }
    }

    [PostgresFact]
    public async Task AssignAndBothExitPathsSerializeInEitherLockOrderWithoutDanglingAssignments()
    {
        var owner = await User("User"); var admin = await User("Admin"); var secondAdmin = await User("Admin");
        using var first = await Login(admin.Phone); using var second = await Login(secondAdmin.Phone);
        foreach (var cancel in new[] { false, true })
        foreach (var assignFirst in new[] { true, false })
        {
            var order = await Appointment(owner.Id); var worker = await Employee("Concurrent"); var version = await Version(first, order);
            await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await using var gate = new NpgsqlConnection(Environment.GetEnvironmentVariable("AUTOBODY_IDENTITY_POSTGRES")); await gate.OpenAsync();
            const int lockKey = 735121;
            await using (var command = new NpgsqlCommand($"SELECT pg_advisory_lock({lockKey})", gate)) await command.ExecuteNonQueryAsync();
            if (assignFirst)
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE FUNCTION assignment_race_gate_test() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN PERFORM pg_advisory_xact_lock(735121); RETURN NEW; END $$;
                    CREATE TRIGGER assignment_race_gate_test BEFORE INSERT ON "EmployeeAssignments" FOR EACH ROW EXECUTE FUNCTION assignment_race_gate_test();
                    """);
            else
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE FUNCTION assignment_race_gate_test() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN IF OLD."Status" = 2 AND NEW."Status" <> 2 THEN PERFORM pg_advisory_xact_lock(735121); END IF; RETURN NEW; END $$;
                    CREATE TRIGGER assignment_race_gate_test BEFORE UPDATE ON "Appointments" FOR EACH ROW EXECUTE FUNCTION assignment_race_gate_test();
                    """);
            Task<HttpResponseMessage>? leading = null; Task<HttpResponseMessage>? trailing = null;
            try
            {
                leading = assignFirst ? Assign(first, order, worker) : Exit(first, order, version, cancel);
                var waiting = false;
                for (var attempt = 0; attempt < 150 && !waiting; attempt++)
                {
                    await using var check = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND objid = {lockKey})", gate);
                    waiting = (bool)(await check.ExecuteScalarAsync())!;
                    if (!waiting) await Task.Delay(40);
                }
                Assert.True(waiting, "Expected the leading request to hold the appointment lock and wait at the test gate.");
                trailing = assignFirst ? Exit(second, order, version, cancel) : Assign(second, order, worker);
                var rowBlocked = false;
                for (var attempt = 0; attempt < 150 && !rowBlocked; attempt++)
                {
                    await using var check = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query LIKE '%FROM \"Appointments\"%' AND query LIKE '%FOR UPDATE%')", gate);
                    rowBlocked = (bool)(await check.ExecuteScalarAsync())!;
                    if (!rowBlocked) await Task.Delay(40);
                }
                Assert.True(rowBlocked, "Expected the competing operation to wait on the appointment row.");
                await using (var unlock = new NpgsqlCommand($"SELECT pg_advisory_unlock({lockKey})", gate)) await unlock.ExecuteNonQueryAsync();
                using var leadResult = await leading; using var trailResult = await trailing;
                Assert.Equal(assignFirst ? HttpStatusCode.Created : HttpStatusCode.NoContent, leadResult.StatusCode);
                Assert.Equal(assignFirst ? HttpStatusCode.NoContent : HttpStatusCode.Conflict, trailResult.StatusCode);
                var expected = cancel ? AppointmentStatus.Cancelled : AppointmentStatus.AwaitingPayment;
                Assert.Equal(expected, (await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == order)).Status);
                var history = await db.EmployeeAssignments.AsNoTracking().Where(a => a.AppointmentId == order).ToListAsync();
                Assert.Equal(assignFirst ? 1 : 0, history.Count);
                Assert.All(history, a => Assert.NotNull(a.ReleasedAtUtc));
                Assert.Single(await db.AppointmentStatusEvents.AsNoTracking().Where(e => e.AppointmentId == order && e.ToStatus == expected).ToListAsync());
                Assert.Single(await db.AdminAuditEvents.AsNoTracking().Where(e => e.ObjectId == order.ToString() && e.Action == "AppointmentStatusChanged").ToListAsync());
            }
            finally
            {
                await using (var unlock = new NpgsqlCommand($"SELECT pg_advisory_unlock({lockKey})", gate)) await unlock.ExecuteNonQueryAsync();
                if (leading is not null) await leading; if (trailing is not null) await trailing;
                if (assignFirst) await db.Database.ExecuteSqlRawAsync("DROP TRIGGER assignment_race_gate_test ON \"EmployeeAssignments\"; DROP FUNCTION assignment_race_gate_test();");
                else await db.Database.ExecuteSqlRawAsync("DROP TRIGGER assignment_race_gate_test ON \"Appointments\"; DROP FUNCTION assignment_race_gate_test();");
            }
        }
    }
}
