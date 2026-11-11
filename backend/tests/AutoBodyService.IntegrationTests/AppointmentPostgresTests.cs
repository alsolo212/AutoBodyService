using AutoBodyService.Domain.Appointments;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AutoBodyService.IntegrationTests;

[Collection("PostgresIdentity")]
public sealed class AppointmentPostgresTests(PostgresIdentityFixture fixture)
{
    private static readonly DateTimeOffset Created = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Slot = new(2026, 1, 2, 10, 0, 0, TimeSpan.Zero);
    private static Appointment Appointment(string owner, DateTimeOffset slot) => new(Guid.NewGuid(), owner, slot, "1HGCM82633A123456", "Test", "Model", "Problem", Created);

    [PostgresFact]
    public async Task InitialEventPersistsAndCancelledSlotCanBeRebooked()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var owner = await CreateOwner(scope.ServiceProvider);
        var appointment = Appointment(owner, Slot);
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var loaded = await db.Appointments.Include(a => a.StatusEvents).SingleAsync(a => a.Id == appointment.Id);
        var created = Assert.Single(loaded.StatusEvents);
        Assert.Equal(AppointmentStatus.AwaitingInspection, created.ToStatus);
        Assert.Null(created.FromStatus);
        Assert.Equal(owner, created.ActorId);
        Assert.Equal(Created, created.ChangedAtUtc);
        Assert.Equal(1, await db.AppointmentStatusEvents.CountAsync(e => e.AppointmentId == appointment.Id));
        db.Appointments.Add(Appointment(owner, Slot));
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(duplicate.InnerException).SqlState);
        db.ChangeTracker.Clear();
        loaded = await db.Appointments.SingleAsync(a => a.Id == appointment.Id);
        db.Entry(loaded).Property(a => a.Status).CurrentValue = AppointmentStatus.Cancelled;
        await db.SaveChangesAsync();
        var replacement = Appointment(owner, Slot);
        db.Appointments.Add(replacement);
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.Appointments.CountAsync(a => a.InspectionStartUtc == Slot));
        Assert.Equal(1, await db.AppointmentStatusEvents.CountAsync(e => e.AppointmentId == appointment.Id));
        Assert.Equal(1, await db.AppointmentStatusEvents.CountAsync(e => e.AppointmentId == replacement.Id));
    }

    [PostgresFact]
    public async Task DatabaseRejectsInvalidTextVinAndStaleChanges()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var owner = await CreateOwner(scope.ServiceProvider);
        var appointment = Appointment(owner, Slot.AddDays(1));
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();
        foreach (var field in new[] { "Make", "Model", "ProblemDescription" })
        foreach (var invalid in new[] { "", " \t\n" })
        {
            var sql = $"UPDATE \"Appointments\" SET \"{field}\" = {{0}} WHERE \"Id\" = {{1}}";
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql, invalid, appointment.Id));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
        var invalidVin = "1HGCM82633A12345I";
        var vinError = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Appointments\" SET \"Vin\" = {invalidVin} WHERE \"Id\" = {appointment.Id}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, vinError.SqlState);
        await using var secondScope = fixture.Factory.Services.CreateAsyncScope();
        var other = secondScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stale = await other.Appointments.SingleAsync(a => a.Id == appointment.Id);
        db.Entry(appointment).Property(a => a.Status).CurrentValue = AppointmentStatus.Scheduled;
        await db.SaveChangesAsync();
        other.Entry(stale).Property(a => a.Status).CurrentValue = AppointmentStatus.InProgress;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => other.SaveChangesAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(AppointmentStatus.Scheduled, (await db.Appointments.SingleAsync(a => a.Id == appointment.Id)).Status);
    }

    [PostgresFact]
    public async Task ConcurrentDomainCommandsCannotOverwriteTheFirstTransition()
    {
        await using var firstScope = fixture.Factory.Services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var owner = await CreateOwner(firstScope.ServiceProvider);
        var appointment = Appointment(owner, Slot.AddDays(2));
        first.Appointments.Add(appointment);
        await first.SaveChangesAsync();

        await using var secondScope = fixture.Factory.Services.CreateAsyncScope();
        var second = secondScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stale = await second.Appointments.SingleAsync(a => a.Id == appointment.Id);
        appointment.Schedule(new RepairPlan(appointment.Id, 1m, Slot, Slot.AddDays(1), Slot), true);
        Assert.NotNull(stale.CancelByOwner(Created));

        await first.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        first.ChangeTracker.Clear();
        Assert.Equal(AppointmentStatus.Scheduled, (await first.Appointments.SingleAsync(a => a.Id == appointment.Id)).Status);
        Assert.Single(await first.AppointmentStatusEvents.Where(e => e.AppointmentId == appointment.Id).ToListAsync());
    }

    private static async Task<string> CreateOwner(IServiceProvider services)
    {
        var users = services.GetRequiredService<UserManager<ApplicationUser>>();
        var digits = Random.Shared.NextInt64(100_000_000, 1_000_000_000).ToString();
        var phone = "+380" + digits;
        var user = new ApplicationUser { Name = "Test", UserName = phone, PhoneNumber = phone, PhoneNormalized = phone };
        Assert.True((await users.CreateAsync(user, "Example-password-123!")).Succeeded);
        return user.Id;
    }
}
