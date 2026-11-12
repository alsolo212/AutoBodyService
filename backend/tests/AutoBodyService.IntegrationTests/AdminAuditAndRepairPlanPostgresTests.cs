using AutoBodyService.Application.Audit;
using AutoBodyService.Application.InspectionSchedule;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Domain.InspectionSchedule;
using AutoBodyService.Infrastructure.Audit;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.InspectionSchedule;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Text.Json;

namespace AutoBodyService.IntegrationTests;

[Collection("PostgresIdentity")]
public sealed class AdminAuditAndRepairPlanPostgresTests(PostgresIdentityFixture fixture)
{
    private static readonly DateTimeOffset Moment = new(2045, 1, 2, 10, 0, 0, TimeSpan.Zero);

    private async Task<string> CreateActorAsync(IServiceProvider services)
    {
        var users = services.GetRequiredService<UserManager<ApplicationUser>>();
        var phone = "+380" + Random.Shared.NextInt64(100_000_000, 1_000_000_000);
        var actor = new ApplicationUser { Name = "Test", UserName = phone, PhoneNumber = phone, PhoneNormalized = phone };
        Assert.True((await users.CreateAsync(actor, "Example-password-123!")).Succeeded);
        return actor.Id;
    }

    [PostgresFact]
    public async Task AuditFailureRollsBackWeeklyAndDateChanges()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var actor = await CreateActorAsync(scope.ServiceProvider);
        var weekly = scope.ServiceProvider.GetRequiredService<IInspectionScheduleService>();
        var exceptions = scope.ServiceProvider.GetRequiredService<IInspectionExceptionsService>();
        var calendar = new InspectionCalendarService(db, weekly, exceptions, new FailingAuditWriter(), TimeProvider.System);
        var before = await weekly.GetAsync(default);
        var date = new DateOnly(2077, 5, 23);
        var beforeDate = await exceptions.GetAsync(date, default);
        WeeklyInterval[] intervals = [new(DayOfWeek.Monday, new TimeOnly(8, 0), new TimeOnly(9, 0))];
        var preview = await calendar.ChangeWeeklyAsync(actor, before.Version, intervals, true, null, default);
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => calendar.ChangeWeeklyAsync(actor, before.Version,
            intervals, false, preview.Preview!.ConfirmationToken, default));
        db.ChangeTracker.Clear();
        Assert.Equal(before, await weekly.GetAsync(default), new WeeklyScheduleComparer());
        await Assert.ThrowsAsync<InvalidOperationException>(() => calendar.ChangeDateAsync(actor, before.Version, date,
            new InspectionException(date, DateOverrideMode.Closed, null, [], []), false, null, default));
        db.ChangeTracker.Clear();
        var afterDate = await exceptions.GetAsync(date, default);
        Assert.Equal(before.Version, afterDate.Version);
        Assert.Equal(beforeDate.Exception, afterDate.Exception);
        Assert.Empty(await db.AdminAuditEvents.Where(e => e.ActorId == actor).ToListAsync());
    }

    [PostgresFact]
    public async Task AuditEventsAreAppendOnlyAndWriterRequiresTransaction()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var actor = await CreateActorAsync(scope.ServiceProvider);
        var writer = scope.ServiceProvider.GetRequiredService<IAdminAuditWriter>();
        Assert.Throws<InvalidOperationException>(() => writer.RecordWeeklyScheduleChange(actor, [], []));
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            writer.RecordWeeklyScheduleChange(actor, [], []);
            writer.RecordAppointmentStatusChange(actor, Guid.NewGuid(), AppointmentStatus.Scheduled, AppointmentStatus.InProgress);
            writer.RecordRepairPlanChange(actor, Guid.NewGuid(), true, false);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        var audit = await db.AdminAuditEvents.SingleAsync(e => e.ActorId == actor && e.Action == "InspectionScheduleChanged");
        var planEvent = await db.AdminAuditEvents.SingleAsync(e => e.ActorId == actor && e.Action == "RepairPlanChanged");
        using var planDetails = JsonDocument.Parse(planEvent.Details);
        Assert.True(planDetails.RootElement.GetProperty("priceChanged").GetBoolean());
        Assert.False(planDetails.RootElement.GetProperty("datesChanged").GetBoolean());
        Assert.Equal(2, planDetails.RootElement.EnumerateObject().Count());
        db.Entry(audit).Property(e => e.Action).CurrentValue = "Changed";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        var alteredAction = "changed";
        var update = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"AdminAuditEvents\" SET \"Action\" = {alteredAction} WHERE \"Id\" = {audit.Id}"));
        Assert.Equal(PostgresErrorCodes.RaiseException, update.SqlState);
        var delete = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AdminAuditEvents\" WHERE \"Id\" = {audit.Id}"));
        Assert.Equal(PostgresErrorCodes.RaiseException, delete.SqlState);
        Assert.Equal("InspectionScheduleChanged", (await db.AdminAuditEvents.SingleAsync(e => e.Id == audit.Id)).Action);
    }

    [PostgresFact]
    public async Task AuditInsertFailureRollsBackBothCalendarOperations()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var weekly = scope.ServiceProvider.GetRequiredService<IInspectionScheduleService>();
        var exceptions = scope.ServiceProvider.GetRequiredService<IInspectionExceptionsService>();
        var calendar = scope.ServiceProvider.GetRequiredService<IInspectionCalendarService>();
        var before = await weekly.GetAsync(default);
        WeeklyInterval[] intervals = [new(DayOfWeek.Tuesday, new TimeOnly(7, 0), new TimeOnly(8, 0))];
        var absentActor = Guid.NewGuid().ToString();
        var preview = await calendar.ChangeWeeklyAsync(absentActor, before.Version, intervals, true, null, default);
        db.ChangeTracker.Clear();
        var weeklyError = await Assert.ThrowsAsync<DbUpdateException>(() => calendar.ChangeWeeklyAsync(absentActor, before.Version, intervals, false, preview.Preview!.ConfirmationToken, default));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(weeklyError.InnerException).SqlState);
        db.ChangeTracker.Clear();
        Assert.Equal(before, await weekly.GetAsync(default), new WeeklyScheduleComparer());
        var date = new DateOnly(2077, 6, 24);
        var old = await exceptions.GetAsync(date, default);
        var rule = new InspectionException(date, DateOverrideMode.Closed, null, [], []);
        var datePreview = await calendar.ChangeDateAsync(absentActor, before.Version, date, rule, true, null, default);
        db.ChangeTracker.Clear();
        var dateError = await Assert.ThrowsAsync<DbUpdateException>(() => calendar.ChangeDateAsync(absentActor, before.Version, date, rule, false, datePreview.Preview!.ConfirmationToken, default));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(dateError.InnerException).SqlState);
        db.ChangeTracker.Clear();
        var after = await exceptions.GetAsync(date, default);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(old.Exception, after.Exception);
        Assert.Empty(await db.AdminAuditEvents.Where(e => e.ActorId == absentActor).ToListAsync());
    }

    [PostgresFact]
    public async Task RepairPlanRoundTripsRevisionAndDatabaseRejectsInvalidPriceDatesAndCurrency()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var owner = await CreateActorAsync(scope.ServiceProvider);
        var appointment = new Appointment(Guid.NewGuid(), owner, Moment, "1HGCM82633A123456", "Test", "Model", "Issue", Moment.AddDays(-1));
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();
        Assert.Null(appointment.RepairPlan);
        appointment.Schedule(new RepairPlan(appointment.Id, 0m, Moment, Moment, Moment), true);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var saved = await db.Appointments.Include(a => a.RepairPlan).SingleAsync(a => a.Id == appointment.Id);
        Assert.Equal(0m, saved.RepairPlan!.AgreedPrice);
        Assert.Equal("UAH", saved.RepairPlan.Currency);
        saved.ReviseRepairPlan(12.34m, Moment.AddDays(1), Moment.AddDays(2), Moment.AddDays(1));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        saved = await db.Appointments.Include(a => a.RepairPlan).SingleAsync(a => a.Id == appointment.Id);
        Assert.Equal(AppointmentStatus.Scheduled, saved.Status);
        Assert.Equal(12.34m, saved.RepairPlan!.AgreedPrice);
        Assert.Equal(Moment.AddDays(2), saved.RepairPlan.EstimatedFinishUtc);
        foreach (var sql in new[] {
            "UPDATE \"RepairPlan\" SET \"AgreedPrice\" = -1 WHERE \"AppointmentId\" = {0}",
            "UPDATE \"RepairPlan\" SET \"AgreedPrice\" = 'NaN'::numeric WHERE \"AppointmentId\" = {0}",
            "UPDATE \"RepairPlan\" SET \"AgreedPrice\" = 'Infinity'::numeric WHERE \"AppointmentId\" = {0}",
            "UPDATE \"RepairPlan\" SET \"EstimatedFinishUtc\" = \"PlannedStartUtc\" - interval '1 second' WHERE \"AppointmentId\" = {0}",
            "UPDATE \"RepairPlan\" SET \"Currency\" = 'USD' WHERE \"AppointmentId\" = {0}" })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql, appointment.Id));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }

        await using var otherScope = fixture.Factory.Services.CreateAsyncScope();
        var other = otherScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stale = await other.Appointments.Include(a => a.RepairPlan).SingleAsync(a => a.Id == appointment.Id);
        saved.StartRepair();
        await db.SaveChangesAsync();
        stale.ReviseRepairPlan(50m, Moment, Moment, Moment.AddDays(2));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => other.SaveChangesAsync());
        db.ChangeTracker.Clear();
        var latest = await db.Appointments.Include(a => a.RepairPlan).SingleAsync(a => a.Id == appointment.Id);
        Assert.Equal(AppointmentStatus.InProgress, latest.Status);
        Assert.Equal(12.34m, latest.RepairPlan!.AgreedPrice);
    }

    private sealed class FailingAuditWriter : IAdminAuditWriter
    {
        public void RecordWeeklyScheduleChange(string actorId, IReadOnlyList<WeeklyInterval> before, IReadOnlyList<WeeklyInterval> after) => throw new InvalidOperationException("Audit unavailable");
        public void RecordDateExceptionChange(string actorId, DateOnly date, InspectionException? before, InspectionException? after) => throw new InvalidOperationException("Audit unavailable");
        public void RecordAppointmentStatusChange(string actorId, Guid appointmentId, AppointmentStatus from, AppointmentStatus to) => throw new InvalidOperationException("Audit unavailable");
        public void RecordRepairPlanChange(string actorId, Guid appointmentId, bool priceChanged, bool datesChanged) => throw new InvalidOperationException("Audit unavailable");
    }

    private sealed class WeeklyScheduleComparer : IEqualityComparer<WeeklySchedule>
    {
        public bool Equals(WeeklySchedule? x, WeeklySchedule? y) => x is not null && y is not null && x.Version == y.Version && x.Intervals.SequenceEqual(y.Intervals);
        public int GetHashCode(WeeklySchedule obj) => obj.Version.GetHashCode();
    }
}
