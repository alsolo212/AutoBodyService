using System.Security.Cryptography;
using AutoBodyService.Application.Appointments;
using AutoBodyService.Application.InspectionSchedule;
using AutoBodyService.Application.Notifications;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Infrastructure.Appointments;
using AutoBodyService.Infrastructure.Audit;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.Notifications;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace AutoBodyService.IntegrationTests;

[Collection("PostgresIdentity")]
public sealed class InspectionReminderPostgresTests(PostgresIdentityFixture fixture)
{
    private static readonly DateTimeOffset Base = new(2031, 1, 1, 10, 0, 0, TimeSpan.Zero);
    private sealed class Available(DateTimeOffset start) : IInspectionSlotsService
    {
        public Task<IReadOnlyList<InspectionSlot>> GetAvailableAsync(DateOnly date, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<InspectionSlot>>([new(start, start.AddMinutes(30))]);
    }
    private sealed class CallbackPublisher(Func<IReadOnlyCollection<string>, Task> callback) : INotificationRealtimePublisher
    {
        public Task PublishAsync(IReadOnlyCollection<string> recipients) => callback(recipients);
    }
    private async Task<string> User(string role, bool blocked = false)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var phone = "+380" + RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000);
        var user = new ApplicationUser { Name = "Test", UserName = phone, PhoneNumber = phone, PhoneNormalized = phone, IsBlocked = blocked };
        Assert.True((await users.CreateAsync(user, "Example-password-123!")).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return user.Id;
    }
    private static DateTimeOffset RandomStart() => Base.AddSeconds(RandomNumberGenerator.GetInt32(0, 864000));
    private async Task<Guid> Book(string owner, DateTimeOffset start, DateTimeOffset now)
    {
        var clock = new FakeTimeProvider(now);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var service = new CreateAppointmentService(new AppointmentCreationStore(db, new Available(start), clock), clock);
        var result = await service.CreateAsync(new(owner, start, "1HGCM82633A123456", "Test", "Model", "Issue"), default);
        Assert.Equal(CreateAppointmentStatus.Created, result.Status);
        return result.Id!.Value;
    }
    private async Task<(Guid Appointment, Guid Reminder)> Seed(string owner, DateTimeOffset start, string type, TimeSpan lead)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var appointment = new Appointment(Guid.NewGuid(), owner, start, "1HGCM82633A123456", "Test", "Model", "Issue", start.AddDays(-3));
        var reminder = new ScheduledReminder { Id = Guid.NewGuid(), AppointmentId = appointment.Id, RecipientId = type == "Admin30Minutes" ? null : owner,
            Type = type, DueAtUtc = start - lead, EventKey = $"test:{Guid.NewGuid()}" };
        db.Appointments.Add(appointment); db.ScheduledReminders.Add(reminder); await db.SaveChangesAsync();
        return (appointment.Id, reminder.Id);
    }
    private async Task Process(FakeTimeProvider clock, DateTimeOffset started)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await new ReminderProcessor(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), clock).ProcessDueAsync(started, default);
    }

    [PostgresFact]
    public async Task MigrationBackfillsExistingClientJobsAndPreservesAdminAudience()
    {
        var schema = "reminder_upgrade_" + Guid.NewGuid().ToString("N");
        var baseConnection = Environment.GetEnvironmentVariable("AUTOBODY_IDENTITY_POSTGRES")!;
        await using var admin = new NpgsqlConnection(baseConnection); await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(baseConnection) { SearchPath = schema }.ConnectionString;
            await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connection).Options);
            var migrator = db.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20261113104457_NotificationOptionalAppointment");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "AspNetUsers" ("Id", "Name", "PhoneNormalized", "UserName", "PhoneNumber", "MustChangePassword", "IsBlocked", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
                VALUES ('legacy-user', 'Test', '+380000000001', '+380000000001', '+380000000001', false, false, false, false, false, false, 0);
                INSERT INTO "Appointments" ("Id", "UserId", "InspectionStartUtc", "Vin", "Make", "Model", "ProblemDescription", "Status", "CreatedAtUtc")
                VALUES ('10000000-0000-0000-0000-000000000000', 'legacy-user', '2032-01-01 10:00:00+00', '1HGCM82633A123456', 'Test', 'Model', 'Issue', 0, '2031-12-01 00:00:00+00');
                INSERT INTO "ScheduledReminders" ("Id", "AppointmentId", "Type", "DueAtUtc", "ProcessedAtUtc", "EventKey") VALUES
                ('20000000-0000-0000-0000-000000000000', '10000000-0000-0000-0000-000000000000', 'Client24Hours', '2031-12-31 10:00:00+00', NULL, 'legacy-client'),
                ('30000000-0000-0000-0000-000000000000', '10000000-0000-0000-0000-000000000000', 'Admin30Minutes', '2032-01-01 09:30:00+00', NULL, 'legacy-admin'),
                ('40000000-0000-0000-0000-000000000000', '10000000-0000-0000-0000-000000000000', 'Client2Hours', '2032-01-01 08:00:00+00', '2032-01-01 08:00:00+00', 'legacy-processed');
                """);
            await migrator.MigrateAsync();
            var rows = await db.ScheduledReminders.AsNoTracking().ToDictionaryAsync(r => r.EventKey);
            Assert.Equal("legacy-user", rows["legacy-client"].RecipientId);
            Assert.Null(rows["legacy-client"].Outcome);
            Assert.Null(rows["legacy-admin"].RecipientId);
            Assert.Equal("ProcessedBeforeUpgrade", rows["legacy-processed"].Outcome);
            Assert.Equal("legacy-user", rows["legacy-processed"].RecipientId);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task BookingSchedulesOnlyStrictlyFutureThresholdsAndAssignsOwner()
    {
        var owner = await User("User");
        var scenarios = new[] {
            (TimeSpan.FromHours(25), new[] { "Admin30Minutes", "Client24Hours", "Client2Hours" }),
            (TimeSpan.FromHours(24).Add(TimeSpan.FromSeconds(1)), new[] { "Admin30Minutes", "Client24Hours", "Client2Hours" }),
            (TimeSpan.FromHours(24), new[] { "Admin30Minutes", "Client2Hours" }),
            (TimeSpan.FromHours(24).Subtract(TimeSpan.FromSeconds(1)), new[] { "Admin30Minutes", "Client2Hours" }),
            (TimeSpan.FromHours(3), new[] { "Admin30Minutes", "Client2Hours" }),
            (TimeSpan.FromHours(2).Add(TimeSpan.FromSeconds(1)), new[] { "Admin30Minutes", "Client2Hours" }),
            (TimeSpan.FromHours(2), new[] { "Admin30Minutes" }),
            (TimeSpan.FromHours(2).Subtract(TimeSpan.FromSeconds(1)), new[] { "Admin30Minutes" }),
            (TimeSpan.FromMinutes(45), new[] { "Admin30Minutes" }),
            (TimeSpan.FromMinutes(30).Add(TimeSpan.FromSeconds(1)), new[] { "Admin30Minutes" }),
            (TimeSpan.FromMinutes(30), Array.Empty<string>()),
            (TimeSpan.FromMinutes(30).Subtract(TimeSpan.FromSeconds(1)), Array.Empty<string>()),
            (TimeSpan.FromMinutes(10), Array.Empty<string>()) };
        foreach (var (before, expected) in scenarios)
        {
            var start = RandomStart(); var id = await Book(owner, start, start - before);
            await using var scope = fixture.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var reminders = await db.ScheduledReminders.AsNoTracking().Where(r => r.AppointmentId == id).ToListAsync();
            Assert.Equal(expected.Order(), reminders.Select(r => r.Type).Order());
            Assert.All(reminders, r => { Assert.True(r.DueAtUtc > start - before); Assert.Equal(r.Type == "Admin30Minutes" ? null : owner, r.RecipientId); });
        }
    }

    [PostgresFact]
    public async Task TwoWorkersAndRestartProduceOneCommittedNotificationPerJob()
    {
        var owner = await User("User"); var start = new DateTimeOffset(2027, 1, 2, 10, 0, 0, TimeSpan.Zero).AddSeconds(RandomNumberGenerator.GetInt32(0, 86400));
        var pair = await Seed(owner, start, "Client2Hours", TimeSpan.FromHours(2));
        var due = start.AddHours(-2); var clock = new FakeTimeProvider(due);
        var tag = "reminder-" + Guid.NewGuid().ToString("N");
        var baseConnection = Environment.GetEnvironmentVariable("AUTOBODY_IDENTITY_POSTGRES")!;
        var firstConnection = new NpgsqlConnectionStringBuilder(baseConnection) { ApplicationName = tag + "a" }.ConnectionString;
        var secondConnection = new NpgsqlConnectionStringBuilder(baseConnection) { ApplicationName = tag + "b" }.ConnectionString;
        await using var firstDb = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(firstConnection).Options);
        await using var secondDb = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(secondConnection).Options);
        await using var blockScope = fixture.Factory.Services.CreateAsyncScope();
        var blocker = blockScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Appointments\" WHERE \"Id\" = {pair.Appointment} FOR UPDATE");
        var first = new ReminderProcessor(firstDb, clock).ProcessDueAsync(due.AddMinutes(-1), default);
        var second = new ReminderProcessor(secondDb, clock).ProcessDueAsync(due.AddMinutes(-1), default);
        await using var observer = new NpgsqlConnection(baseConnection); await observer.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE application_name IN (@a, @b) AND wait_event_type = 'Lock'", observer);
        command.Parameters.AddWithValue("a", tag + "a"); command.Parameters.AddWithValue("b", tag + "b");
        long waiting = 0;
        for (var tries = 0; tries < 200 && waiting < 2; tries++)
        {
            waiting = (long)(await command.ExecuteScalarAsync())!;
            if (waiting < 2) await Task.Delay(25);
        }
        Assert.Equal(2, waiting);
        await transaction.CommitAsync();
        await Task.WhenAll(first, second);
        await Process(clock, due.AddSeconds(1));
        await using var scope = fixture.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var reminder = await db.ScheduledReminders.SingleAsync(r => r.Id == pair.Reminder);
        Assert.Equal("Sent", reminder.Outcome); Assert.Equal(due, reminder.ProcessedAtUtc);
        var notification = Assert.Single(await db.Notifications.Where(n => n.AppointmentId == pair.Appointment).ToListAsync());
        Assert.Equal(owner, notification.RecipientId); Assert.Equal(NotificationAudience.Personal, notification.Audience); Assert.Equal($"reminder:{pair.Reminder}:{owner}", notification.EventKey);
        db.Notifications.Add(new Notification { Id = Guid.NewGuid(), AppointmentId = pair.Appointment, RecipientId = owner, Audience = NotificationAudience.Personal, Type = "Test", Title = "Test", Message = "Test", CreatedAtUtc = due, EventKey = notification.EventKey });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        await using var duplicateScope = fixture.Factory.Services.CreateAsyncScope();
        var duplicateDb = duplicateScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        duplicateDb.ScheduledReminders.Add(new ScheduledReminder { Id = Guid.NewGuid(), AppointmentId = pair.Appointment, RecipientId = owner,
            Type = "Client2Hours", DueAtUtc = due, EventKey = reminder.EventKey });
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => duplicateDb.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(duplicate.InnerException).SqlState);
    }

    [PostgresFact]
    public async Task StartupDowntimeExcessiveLatenessPastInspectionAndInvalidStateAreSkipped()
    {
        var owner = await User("User"); var start = RandomStart(); var due = start.AddHours(-2);
        var old = await Seed(owner, start, "Client2Hours", TimeSpan.FromHours(2));
        var lateStart = new FakeTimeProvider(due.AddSeconds(5));
        await Process(lateStart, due.AddSeconds(1));
        var late = await Seed(owner, start.AddDays(11), "Client2Hours", TimeSpan.FromHours(2));
        var lateDue = start.AddDays(11).AddHours(-2); var lateClock = new FakeTimeProvider(lateDue.AddSeconds(31));
        await Process(lateClock, lateDue.AddHours(-1));
        var past = await Seed(owner, start.AddDays(12), "Client2Hours", TimeSpan.FromHours(2));
        var pastClock = new FakeTimeProvider(start.AddDays(12)); await Process(pastClock, pastClock.GetUtcNow());
        var changed = await Seed(owner, start.AddDays(13), "Client2Hours", TimeSpan.FromHours(2));
        await using (var changeScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var changeDb = changeScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var appointment = await changeDb.Appointments.SingleAsync(a => a.Id == changed.Appointment);
            changeDb.Entry(appointment).Property(a => a.Status).CurrentValue = AppointmentStatus.Scheduled;
            await changeDb.SaveChangesAsync();
        }
        var changedDue = start.AddDays(13).AddHours(-2);
        await Process(new FakeTimeProvider(changedDue), changedDue.AddSeconds(-1));
        await using var scope = fixture.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal("SkippedLate", (await db.ScheduledReminders.SingleAsync(r => r.Id == old.Reminder)).Outcome);
        Assert.Equal("SkippedLate", (await db.ScheduledReminders.SingleAsync(r => r.Id == late.Reminder)).Outcome);
        Assert.Equal("SkippedPast", (await db.ScheduledReminders.SingleAsync(r => r.Id == past.Reminder)).Outcome);
        Assert.Equal("SkippedState", (await db.ScheduledReminders.SingleAsync(r => r.Id == changed.Reminder)).Outcome);
        Assert.Empty(await db.Notifications.Where(n => n.AppointmentId == old.Appointment || n.AppointmentId == late.Appointment || n.AppointmentId == past.Appointment || n.AppointmentId == changed.Appointment).ToListAsync());
    }

    [PostgresFact]
    public async Task DueAndLatenessBoundariesDoNotFireEarlyOrReplayStaleJobs()
    {
        var owner = await User("User"); var start = RandomStart().AddDays(180);
        var early = await Seed(owner, start, "Client2Hours", TimeSpan.FromHours(2)); var due = start.AddHours(-2);
        var clock = new FakeTimeProvider(due.AddTicks(-10)); await Process(clock, due.AddMinutes(-1));
        await using (var before = fixture.Factory.Services.CreateAsyncScope())
            Assert.Null((await before.ServiceProvider.GetRequiredService<ApplicationDbContext>().ScheduledReminders.SingleAsync(r => r.Id == early.Reminder)).ProcessedAtUtc);
        clock.Advance(TimeSpan.FromTicks(10)); await Process(clock, due);
        var boundary = await Seed(owner, start.AddDays(1), "Client2Hours", TimeSpan.FromHours(2)); var boundaryDue = start.AddDays(1).AddHours(-2);
        await Process(new FakeTimeProvider(boundaryDue.AddSeconds(30)), boundaryDue.AddSeconds(-1));
        var stale = await Seed(owner, start.AddDays(2), "Client2Hours", TimeSpan.FromHours(2)); var staleDue = start.AddDays(2).AddHours(-2);
        await Process(new FakeTimeProvider(staleDue.AddSeconds(30).AddTicks(10)), staleDue.AddSeconds(-1));
        await using var scope = fixture.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal("Sent", (await db.ScheduledReminders.SingleAsync(r => r.Id == early.Reminder)).Outcome);
        Assert.Equal("Sent", (await db.ScheduledReminders.SingleAsync(r => r.Id == boundary.Reminder)).Outcome);
        Assert.Equal("SkippedLate", (await db.ScheduledReminders.SingleAsync(r => r.Id == stale.Reminder)).Outcome);
        Assert.Empty(await db.Notifications.Where(n => n.AppointmentId == stale.Appointment).ToListAsync());
    }

    [PostgresFact]
    public async Task ClientAndOperatorCancellationBeforeOrAfterDueSkipRemainingJobs()
    {
        var owner = await User("User"); var actor = await User("Admin");
        for (var i = 0; i < 4; i++)
        {
            var start = RandomStart().AddDays(i * 20); var item = await Seed(owner, start, "Client2Hours", TimeSpan.FromHours(2));
            var due = start.AddHours(-2); var clock = new FakeTimeProvider(due.AddSeconds(i % 2 == 0 ? -1 : 1));
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                if (i < 2) Assert.Equal(CancelAppointmentResult.Cancelled, await new CancelAppointmentService(db, clock).CancelOwnAsync(item.Appointment, owner, default));
                else
                {
                    var appointment = await db.Appointments.SingleAsync(a => a.Id == item.Appointment);
                    var version = db.Entry(appointment).Property<uint>("Version").CurrentValue;
                    var store = new AdminAppointmentStore(db, new AdminAuditWriter(db, clock), clock);
                    Assert.Equal(AdminAppointmentResult.Success, await new AdminAppointmentCommands(store).CancelAsync(item.Appointment, version, actor, true, "Test", default));
                }
            }
            await Process(clock, due.AddHours(-1));
            await using var verifyScope = fixture.Factory.Services.CreateAsyncScope(); var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var reminder = await verify.ScheduledReminders.SingleAsync(r => r.Id == item.Reminder);
            Assert.Equal("SkippedCancelled", reminder.Outcome); Assert.NotNull(reminder.ProcessedAtUtc);
            Assert.Empty(await verify.Notifications.Where(n => n.AppointmentId == item.Appointment && n.Type.Contains("Reminder")).ToListAsync());
            var cancellation = await verify.Notifications.Where(n => n.AppointmentId == item.Appointment).ToListAsync();
            Assert.Single(cancellation, n => n.RecipientId == owner && n.Type == "AppointmentCancelled");
            if (i < 2) Assert.Single(cancellation, n => n.RecipientId == actor && n.Type == "AdminAppointmentCancelled");
            else Assert.DoesNotContain(cancellation, n => n.Type == "AdminAppointmentCancelled");
        }
    }

    [PostgresFact]
    public async Task CancellationAndWorkerSerializeInBothLockOrders()
    {
        var owner = await User("User");
        foreach (var cancelFirst in new[] { true, false })
        {
            var start = new DateTimeOffset(2027, 2, 1, 10, 0, 0, TimeSpan.Zero).AddSeconds(RandomNumberGenerator.GetInt32(0, 86400));
            var item = await Seed(owner, start, "Client2Hours", TimeSpan.FromHours(2));
            var due = start.AddHours(-2); var clock = new FakeTimeProvider(due);
            var tag = "reminder-race-" + Guid.NewGuid().ToString("N");
            var source = Environment.GetEnvironmentVariable("AUTOBODY_IDENTITY_POSTGRES")!;
            var cancelConnection = new NpgsqlConnectionStringBuilder(source) { ApplicationName = tag + "c" }.ConnectionString;
            var workerConnection = new NpgsqlConnectionStringBuilder(source) { ApplicationName = tag + "w" }.ConnectionString;
            await using var cancelDb = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(cancelConnection).Options);
            await using var workerDb = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(workerConnection).Options);
            await using var blockScope = fixture.Factory.Services.CreateAsyncScope(); var blocker = blockScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await using var held = await blocker.Database.BeginTransactionAsync();
            await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Appointments\" WHERE \"Id\" = {item.Appointment} FOR UPDATE");
            await using var observer = new NpgsqlConnection(source); await observer.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE application_name IN (@a, @b) AND wait_event_type = 'Lock'", observer);
            command.Parameters.AddWithValue("a", tag + "c"); command.Parameters.AddWithValue("b", tag + "w");
            async Task WaitForLocks(long target)
            {
                long waiting = 0;
                for (var tries = 0; tries < 200 && waiting < target; tries++)
                {
                    waiting = (long)(await command.ExecuteScalarAsync())!;
                    if (waiting < target) await Task.Delay(25);
                }
                Assert.Equal(target, waiting);
            }
            Task<CancelAppointmentResult>? cancellation = null; Task<int>? processing = null;
            if (cancelFirst) cancellation = new CancelAppointmentService(cancelDb, clock).CancelOwnAsync(item.Appointment, owner, default);
            else processing = new ReminderProcessor(workerDb, clock).ProcessDueAsync(due.AddMinutes(-1), default);
            await WaitForLocks(1);
            if (cancelFirst) processing = new ReminderProcessor(workerDb, clock).ProcessDueAsync(due.AddMinutes(-1), default);
            else cancellation = new CancelAppointmentService(cancelDb, clock).CancelOwnAsync(item.Appointment, owner, default);
            await WaitForLocks(2);
            await held.CommitAsync();
            Assert.Equal(CancelAppointmentResult.Cancelled, await cancellation!); await processing!;
            await using var verifyScope = fixture.Factory.Services.CreateAsyncScope(); var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(AppointmentStatus.Cancelled, (await verify.Appointments.SingleAsync(a => a.Id == item.Appointment)).Status);
            var reminder = await verify.ScheduledReminders.SingleAsync(r => r.Id == item.Reminder);
            Assert.Equal(cancelFirst ? "SkippedCancelled" : "Sent", reminder.Outcome);
            var notifications = await verify.Notifications.Where(n => n.AppointmentId == item.Appointment && n.Type == "InspectionReminder2Hours").ToListAsync();
            Assert.Equal(cancelFirst ? 0 : 1, notifications.Count);
        }
    }

    [PostgresFact]
    public async Task FailedCancellationRollsBackReminderSkipWithTheAppointment()
    {
        var owner = await User("User"); var start = RandomStart().AddDays(120);
        var item = await Seed(owner, start, "Client2Hours", TimeSpan.FromHours(2));
        var clock = new FakeTimeProvider(start.AddHours(-3));
        await using var triggerScope = fixture.Factory.Services.CreateAsyncScope();
        var db = triggerScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reminder_cancel_reject() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW."Type" = 'AppointmentCancelled' THEN RAISE EXCEPTION 'test'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER reminder_cancel_reject_trigger BEFORE INSERT ON "Notifications" FOR EACH ROW EXECUTE FUNCTION reminder_cancel_reject();
            """);
        try
        {
            await using var cancelScope = fixture.Factory.Services.CreateAsyncScope();
            var service = new CancelAppointmentService(cancelScope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), clock);
            await Assert.ThrowsAsync<DbUpdateException>(() => service.CancelOwnAsync(item.Appointment, owner, default));
            await using var verifyScope = fixture.Factory.Services.CreateAsyncScope(); var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(AppointmentStatus.AwaitingInspection, (await verify.Appointments.SingleAsync(a => a.Id == item.Appointment)).Status);
            var reminder = await verify.ScheduledReminders.SingleAsync(r => r.Id == item.Reminder);
            Assert.Null(reminder.ProcessedAtUtc); Assert.Null(reminder.Outcome);
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reminder_cancel_reject_trigger ON \"Notifications\"; DROP FUNCTION reminder_cancel_reject();"); }
    }

    [PostgresFact]
    public async Task CancellationAfterDeliveryKeepsPastNotificationAndSkipsFutureReminder()
    {
        var owner = await User("User"); var start = RandomStart().AddDays(130); var id = await Book(owner, start, start.AddHours(-25));
        var due = start.AddHours(-24); var clock = new FakeTimeProvider(due);
        await Process(clock, due.AddSeconds(-1));
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            Assert.Equal(CancelAppointmentResult.Cancelled, await new CancelAppointmentService(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), clock).CancelOwnAsync(id, owner, default));
        clock.Advance(TimeSpan.FromHours(22)); await Process(clock, due.AddSeconds(-1));
        await using var verifyScope = fixture.Factory.Services.CreateAsyncScope(); var db = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var reminders = await db.ScheduledReminders.Where(r => r.AppointmentId == id).ToListAsync();
        Assert.Equal("Sent", Assert.Single(reminders, r => r.Type == "Client24Hours").Outcome);
        Assert.All(reminders.Where(r => r.Type != "Client24Hours"), r => Assert.Equal("SkippedCancelled", r.Outcome));
        var reminder = Assert.Single(await db.Notifications.Where(n => n.AppointmentId == id && n.Type.Contains("Reminder")).ToListAsync());
        Assert.Equal("InspectionReminder24Hours", reminder.Type);
    }

    [PostgresFact]
    public async Task BlockedClientDoesNotReceiveAStoredReminder()
    {
        var owner = await User("User", blocked: true); var start = RandomStart().AddDays(140);
        var item = await Seed(owner, start, "Client2Hours", TimeSpan.FromHours(2));
        var due = start.AddHours(-2); await Process(new FakeTimeProvider(due), due.AddSeconds(-1));
        await using var scope = fixture.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal("SkippedRecipient", (await db.ScheduledReminders.SingleAsync(r => r.Id == item.Reminder)).Outcome);
        Assert.Empty(await db.Notifications.Where(n => n.AppointmentId == item.Appointment).ToListAsync());
    }

    [PostgresFact]
    public async Task AdminReminderResolvesCurrentAuthorizedRecipientsAndClientUsesOwnAccount()
    {
        var owner = await User("User"); var admin = await User("Admin"); var super = await User("SuperAdmin");
        var blocked = await User("Admin", blocked: true); var otherUser = await User("User"); var start = RandomStart().AddDays(150);
        var item = await Seed(owner, start, "Admin30Minutes", TimeSpan.FromMinutes(30));
        var due = start.AddMinutes(-30); await Process(new FakeTimeProvider(due), due.AddSeconds(-1));
        await using var scope = fixture.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var recipients = await db.Notifications.Where(n => n.AppointmentId == item.Appointment).Select(n => n.RecipientId).ToListAsync();
        Assert.All(await db.Notifications.Where(n => n.AppointmentId == item.Appointment).ToListAsync(), n => Assert.Equal(NotificationAudience.Administrator, n.Audience));
        Assert.Contains(admin, recipients); Assert.Contains(super, recipients);
        Assert.DoesNotContain(blocked, recipients); Assert.DoesNotContain(owner, recipients); Assert.DoesNotContain(otherUser, recipients);
        var allowed = await (from user in db.Users
                             join membership in db.UserRoles on user.Id equals membership.UserId
                             join role in db.Roles on membership.RoleId equals role.Id
                             where !user.IsBlocked && (role.Name == "Admin" || role.Name == "SuperAdmin")
                             select user.Id).Distinct().ToListAsync();
        Assert.All(recipients, recipient => Assert.Contains(recipient, allowed));
    }

    [PostgresFact]
    public async Task UtcDueAndKyivOffsetRemainUnambiguousAcrossBothDstTransitions()
    {
        var owner = await User("User");
        var cases = new[] {
            (new DateTimeOffset(2031, 3, 30, 0, 30, 0, TimeSpan.Zero), "30.03.2031 02:30 (Київ, UTC+02:00)"),
            (new DateTimeOffset(2031, 3, 30, 1, 30, 0, TimeSpan.Zero), "30.03.2031 04:30 (Київ, UTC+03:00)"),
            (new DateTimeOffset(2031, 10, 26, 0, 30, 0, TimeSpan.Zero), "26.10.2031 03:30 (Київ, UTC+03:00)"),
            (new DateTimeOffset(2031, 10, 26, 1, 30, 0, TimeSpan.Zero), "26.10.2031 03:30 (Київ, UTC+02:00)") };
        foreach (var (start, expected) in cases)
        {
            var item = await Seed(owner, start, "Client2Hours", TimeSpan.FromHours(2));
            var due = start.AddHours(-2); await Process(new FakeTimeProvider(due), due.AddSeconds(-1));
            await using var scope = fixture.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var reminder = await db.ScheduledReminders.SingleAsync(r => r.Id == item.Reminder);
            Assert.Equal(due, reminder.DueAtUtc); Assert.Equal("Sent", reminder.Outcome);
            Assert.Contains(expected, (await db.Notifications.SingleAsync(n => n.AppointmentId == item.Appointment)).Message);
        }
    }

    [PostgresFact]
    public async Task NotificationWriteFailureRollsBackProcessedMarkerAndCanRetry()
    {
        var owner = await User("User"); var start = RandomStart().AddDays(160);
        var item = await Seed(owner, start, "Client2Hours", TimeSpan.FromHours(2)); var due = start.AddHours(-2); var clock = new FakeTimeProvider(due);
        await using var scope = fixture.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reminder_test_reject() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW."Type" = 'InspectionReminder2Hours' THEN RAISE EXCEPTION 'test'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER reminder_test_reject_trigger BEFORE INSERT ON "Notifications" FOR EACH ROW EXECUTE FUNCTION reminder_test_reject();
            """);
        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => Process(clock, due.AddSeconds(-1)));
            var reminder = await db.ScheduledReminders.AsNoTracking().SingleAsync(r => r.Id == item.Reminder);
            Assert.Null(reminder.ProcessedAtUtc); Assert.Null(reminder.Outcome);
            Assert.Empty(await db.Notifications.Where(n => n.AppointmentId == item.Appointment).ToListAsync());
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reminder_test_reject_trigger ON \"Notifications\"; DROP FUNCTION reminder_test_reject();"); }
        await Process(clock, due.AddSeconds(-1));
        Assert.Equal("Sent", (await db.ScheduledReminders.AsNoTracking().SingleAsync(r => r.Id == item.Reminder)).Outcome);
        Assert.Single(await db.Notifications.Where(n => n.AppointmentId == item.Appointment).ToListAsync());
    }

    [PostgresFact]
    public async Task RealtimeFailureLeavesCommittedReminderAvailableWithoutDuplicates()
    {
        var owner = await User("User"); var start = RandomStart().AddDays(165);
        var item = await Seed(owner, start, "Client2Hours", TimeSpan.FromHours(2));
        var due = start.AddHours(-2); var clock = new FakeTimeProvider(due); var published = false;
        var publisher = new CallbackPublisher(_ => { published = true; throw new InvalidOperationException("Disconnected"); });
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            await new ReminderProcessor(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), clock, publisher).ProcessDueAsync(due.AddSeconds(-1), default);
        Assert.True(published);
        await Process(clock, due.AddSeconds(1));
        await using var verifyScope = fixture.Factory.Services.CreateAsyncScope(); var db = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal("Sent", (await db.ScheduledReminders.SingleAsync(r => r.Id == item.Reminder)).Outcome);
        Assert.Single(await db.Notifications.Where(n => n.AppointmentId == item.Appointment && n.RecipientId == owner).ToListAsync());
    }

    [PostgresFact]
    public async Task HostedWorkerWritesBeforeSignalWithoutBrowser()
    {
        var owner = await User("User"); var start = RandomStart().AddDays(170);
        var item = await Seed(owner, start, "Client2Hours", TimeSpan.FromHours(2)); var due = start.AddHours(-2);
        var clock = new FakeTimeProvider(due);
        var delivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new CallbackPublisher(async recipients =>
        {
            if (!recipients.Contains(owner)) return;
            await using var scope = fixture.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db.ScheduledReminders.AsNoTracking().SingleAsync(r => r.Id == item.Reminder);
            var saved = await db.Notifications.AsNoTracking().AnyAsync(n => n.AppointmentId == item.Appointment && n.RecipientId == owner);
            delivered.TrySetResult(row.Outcome == "Sent" && saved);
        });
        var services = new ServiceCollection(); services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(Environment.GetEnvironmentVariable("AUTOBODY_IDENTITY_POSTGRES")));
        services.AddSingleton<TimeProvider>(clock); services.AddScoped<ReminderProcessor>();
        services.AddSingleton<INotificationRealtimePublisher>(publisher);
        await using var provider = services.BuildServiceProvider();
        using var worker = new ReminderBackgroundService(provider.GetRequiredService<IServiceScopeFactory>(), clock, NullLogger<ReminderBackgroundService>.Instance);
        await worker.StartAsync(default);
        try { Assert.True(await delivered.Task.WaitAsync(TimeSpan.FromSeconds(15))); }
        finally { await worker.StopAsync(default); }
    }
}
