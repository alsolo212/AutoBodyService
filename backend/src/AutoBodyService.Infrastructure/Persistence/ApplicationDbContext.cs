using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.Notifications;
using AutoBodyService.Infrastructure.Audit;
using AutoBodyService.Domain.InspectionSchedule;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Domain.Employees;
using AutoBodyService.Domain.Assignments;
using WeeklyScheduleEntity = AutoBodyService.Domain.InspectionSchedule.InspectionSchedule;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AutoBodyService.Infrastructure.Persistence;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<WeeklyScheduleEntity> InspectionSchedules => Set<WeeklyScheduleEntity>();
    public DbSet<InspectionWeeklyHours> InspectionWeeklyHours => Set<InspectionWeeklyHours>();

    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<ScheduledReminder> ScheduledReminders => Set<ScheduledReminder>();
    public DbSet<EmployeeAssignment> EmployeeAssignments => Set<EmployeeAssignment>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<AppointmentStatusEvent> AppointmentStatusEvents => Set<AppointmentStatusEvent>();
    public DbSet<PaymentHandoverConfirmation> PaymentHandoverConfirmations => Set<PaymentHandoverConfirmation>();
    public DbSet<ManualContactEvent> ManualContactEvents => Set<ManualContactEvent>();
    public DbSet<AdminAuditEvent> AdminAuditEvents => Set<AdminAuditEvent>();
    public DbSet<InspectionDateOverride> InspectionDateOverrides => Set<InspectionDateOverride>();

    public DbSet<RevokedSession> RevokedSessions => Set<RevokedSession>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RejectAuditMutation();
        ProtectRepairPlanChanges();
        var roleUserIds = ChangedRoleUserIds();
        var users = roleUserIds.Length == 0 ? [] : Users.Where(user => roleUserIds.Contains(user.Id)).ToList();
        var blockedIds = ModifiedUserIds();
        var previousBlocks = blockedIds.Length == 0 ? [] : Users.AsNoTracking()
            .Where(user => blockedIds.Contains(user.Id)).Select(user => new { user.Id, user.IsBlocked }).ToList();
        RevokeChangedSessions(users, previousBlocks.ToDictionary(user => user.Id, user => user.IsBlocked));
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        RejectAuditMutation();
        ProtectRepairPlanChanges();
        var roleUserIds = ChangedRoleUserIds();
        var users = roleUserIds.Length == 0 ? [] : await Users.Where(user => roleUserIds.Contains(user.Id)).ToListAsync(cancellationToken);
        var blockedIds = ModifiedUserIds();
        var previousBlocks = blockedIds.Length == 0 ? [] : await Users.AsNoTracking()
            .Where(user => blockedIds.Contains(user.Id)).Select(user => new { user.Id, user.IsBlocked }).ToListAsync(cancellationToken);
        RevokeChangedSessions(users, previousBlocks.ToDictionary(user => user.Id, user => user.IsBlocked));
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void RejectAuditMutation()
    {
        if (ChangeTracker.Entries<AdminAuditEvent>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Administrative audit events are append-only.");
    }

    // A plan correction must also check the appointment's xmin, so a concurrent
    // status change cannot silently make a previously valid correction stale.
    private void ProtectRepairPlanChanges()
    {
        foreach (var plan in ChangeTracker.Entries<RepairPlan>().Where(e => e.State is EntityState.Added or EntityState.Modified).ToArray())
        {
            var appointment = ChangeTracker.Entries<Appointment>().SingleOrDefault(e => e.Entity.Id == plan.Entity.AppointmentId)
                ?? throw new InvalidOperationException("A repair plan must be changed through its tracked appointment.");
            if (appointment.State != EntityState.Added) appointment.Property(a => a.Status).IsModified = true;
        }
    }

    private string[] ChangedRoleUserIds() => ChangeTracker.Entries<IdentityUserRole<string>>()
        .Where(entry => entry.State is EntityState.Added or EntityState.Deleted)
        .Select(entry => entry.Entity.UserId).Distinct().ToArray();

    private string[] ModifiedUserIds() => ChangeTracker.Entries<ApplicationUser>()
        .Where(entry => entry.State == EntityState.Modified && entry.Property(user => user.IsBlocked).IsModified)
        .Select(entry => entry.Entity.Id).ToArray();

    private void RevokeChangedSessions(List<ApplicationUser> roleUsers, Dictionary<string, bool> previousBlocks)
    {
        var blockedUsers = ChangeTracker.Entries<ApplicationUser>()
            .Where(entry => entry.State == EntityState.Modified && entry.Property(user => user.IsBlocked).IsModified
                && previousBlocks.TryGetValue(entry.Entity.Id, out var wasBlocked) && wasBlocked != entry.Entity.IsBlocked)
            .Select(entry => entry.Entity);
        foreach (var user in roleUsers.Concat(blockedUsers).DistinctBy(user => user.Id))
        {
            user.SecurityStamp = Guid.NewGuid().ToString();
            user.ConcurrencyStamp = Guid.NewGuid().ToString();
        }
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<WeeklyScheduleEntity>(entity =>
        {
            entity.HasKey(schedule => schedule.Id);
            entity.Property(schedule => schedule.Version).IsConcurrencyToken();
            entity.HasMany(schedule => schedule.Hours).WithOne().HasForeignKey(hours => hours.ScheduleId);
            entity.ToTable(table => table.HasCheckConstraint("CK_InspectionSchedule_Singleton", "\"Id\" = 1"));
            entity.HasData(new WeeklyScheduleEntity { Id = 1, Version = new Guid("b1050732-661b-48b8-a1bf-11d338a4b001") });
        });
        builder.Entity<InspectionWeeklyHours>(entity =>
        {
            entity.HasKey(hours => hours.Id);
            entity.ToTable(table => table.HasCheckConstraint("CK_InspectionWeeklyHours_Valid", "\"DayOfWeek\" BETWEEN 0 AND 6 AND \"Start\" < \"End\""));
        });
        builder.Entity<Employee>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FullName).HasMaxLength(Employee.MaxTextLength).IsRequired();
            entity.Property(e => e.Specialization).HasMaxLength(Employee.MaxTextLength).IsRequired();
            entity.Property<uint>("Version").IsRowVersion();
            entity.ToTable(table => table.HasCheckConstraint("CK_Employee_RequiredText",
                "\"FullName\" ~ '[^[:space:]]' AND \"Specialization\" ~ '[^[:space:]]'"));
        });
        builder.Entity<EmployeeAssignment>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.Property(a => a.AssignedById).IsRequired();
            entity.Property(a => a.NameSnapshot).HasMaxLength(Employee.MaxTextLength).IsRequired();
            entity.Property(a => a.SpecializationSnapshot).HasMaxLength(Employee.MaxTextLength).IsRequired();
            entity.HasOne<Appointment>().WithMany().HasForeignKey(a => a.AppointmentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Employee>().WithMany().HasForeignKey(a => a.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(a => a.AssignedById).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(a => new { a.AppointmentId, a.EmployeeId }).IsUnique().HasFilter("\"ReleasedAtUtc\" IS NULL");
            entity.HasIndex(a => new { a.AppointmentId, a.AssignedAtUtc });
            entity.HasIndex(a => new { a.EmployeeId, a.AssignedAtUtc });
            entity.ToTable(t => t.HasCheckConstraint("CK_EmployeeAssignment_ReleasedAt", "\"ReleasedAtUtc\" IS NULL OR \"ReleasedAtUtc\" >= \"AssignedAtUtc\""));
        });
        builder.Entity<Appointment>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.UserId).IsRequired();
            entity.Property(x => x.InspectionStartUtc).IsRequired();
            entity.Property(x => x.CreatedAtUtc).IsRequired();
            entity.Property(x => x.Vin).HasMaxLength(17).IsRequired();
            entity.Property(x => x.Make).IsRequired();
            entity.Property(x => x.Model).IsRequired();
            entity.Property(x => x.ProblemDescription).IsRequired();
            entity.HasOne(x => x.RepairPlan).WithOne().HasForeignKey<RepairPlan>(x => x.AppointmentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.InspectionStartUtc).IsUnique().HasFilter("\"Status\" <> 5");
            entity.HasIndex(x => new { x.UserId, x.InspectionStartUtc });
            entity.HasIndex(x => new { x.Status, x.InspectionStartUtc });
            entity.HasIndex(x => x.CreatedAtUtc);
            entity.Property<uint>("Version").IsRowVersion();
            entity.HasMany(x => x.StatusEvents).WithOne().HasForeignKey(x => x.AppointmentId).OnDelete(DeleteBehavior.Restrict);
            entity.Navigation(x => x.StatusEvents).UsePropertyAccessMode(PropertyAccessMode.Field);
            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_Appointment_Status", "\"Status\" BETWEEN 0 AND 5");
                table.HasCheckConstraint("CK_Appointment_Vin", "\"Vin\" ~ '^[A-HJ-NPR-Z0-9]{17}$'");
                table.HasCheckConstraint("CK_Appointment_RequiredText", "\"Make\" ~ '[^[:space:]]' AND \"Model\" ~ '[^[:space:]]' AND \"ProblemDescription\" ~ '[^[:space:]]'");
            });
        });
        builder.Entity<RepairPlan>(entity =>
        {
            entity.HasKey(x => x.AppointmentId);
            entity.Property(x => x.AgreedPrice).HasColumnType("numeric");
            entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            entity.Property<uint>("Version").IsRowVersion();
            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_RepairPlan_Price", "\"AgreedPrice\" >= 0 AND \"AgreedPrice\" <> 'NaN'::numeric AND \"AgreedPrice\" <> 'Infinity'::numeric");
                table.HasCheckConstraint("CK_RepairPlan_Dates", "\"EstimatedFinishUtc\" >= \"PlannedStartUtc\"");
                table.HasCheckConstraint("CK_RepairPlan_Currency", "\"Currency\" = 'UAH'");
            });
        });
        builder.Entity<AdminAuditEvent>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ActorId).IsRequired();
            entity.Property(x => x.Action).HasMaxLength(100).IsRequired();
            entity.Property(x => x.ObjectType).HasMaxLength(100).IsRequired();
            entity.Property(x => x.ObjectId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Details).HasColumnType("jsonb").IsRequired();
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.OccurredAtUtc);
            entity.HasIndex(x => new { x.Action, x.OccurredAtUtc });
            entity.HasIndex(x => new { x.ActorId, x.OccurredAtUtc });
        });
        builder.Entity<PaymentHandoverConfirmation>(entity =>
        {
            entity.HasKey(x => x.AppointmentId);
            entity.Property(x => x.ActorId).IsRequired();
            entity.HasOne<Appointment>().WithOne().HasForeignKey<PaymentHandoverConfirmation>(x => x.AppointmentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.HandedOverAtUtc);
        });
        builder.Entity<ManualContactEvent>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ActorId).IsRequired();
            entity.HasOne<Appointment>().WithMany().HasForeignKey(x => x.AppointmentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.AppointmentId, x.Reason, x.RecordedAtUtc });
            entity.ToTable(t => t.HasCheckConstraint("CK_ManualContactEvent_Reason", "\"Reason\" BETWEEN 0 AND 3"));
        });
        builder.Entity<Notification>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RecipientId).IsRequired();
            entity.Property(x => x.Type).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Message).HasMaxLength(1000).IsRequired();
            entity.Property(x => x.EventKey).HasMaxLength(256).IsRequired();
            entity.HasIndex(x => x.EventKey).IsUnique();
            entity.HasIndex(x => new { x.RecipientId, x.CreatedAtUtc });
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.RecipientId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Appointment>().WithMany().HasForeignKey(x => x.AppointmentId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ScheduledReminder>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Type).HasMaxLength(100).IsRequired();
            entity.Property(x => x.EventKey).HasMaxLength(256).IsRequired();
            entity.HasIndex(x => x.EventKey).IsUnique();
            entity.HasIndex(x => new { x.ProcessedAtUtc, x.DueAtUtc });
            entity.HasOne<Appointment>().WithMany().HasForeignKey(x => x.AppointmentId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<AppointmentStatusEvent>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ActorId).IsRequired();
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.AppointmentId, x.ChangedAtUtc });
            entity.HasIndex(x => new { x.ToStatus, x.ChangedAtUtc });
            entity.HasIndex(x => x.AppointmentId).IsUnique().HasFilter("\"FromStatus\" IS NULL");
            entity.ToTable(table => table.HasCheckConstraint("CK_AppointmentStatusEvent_Status", "(\"FromStatus\" BETWEEN 0 AND 5 OR \"FromStatus\" IS NULL) AND \"ToStatus\" BETWEEN 0 AND 5 AND (\"FromStatus\" IS NOT NULL OR \"ToStatus\" = 0)"));
        });
        builder.Entity<InspectionDateOverride>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Date).IsUnique();
            entity.Property(x => x.Reason).HasMaxLength(500);
            entity.HasMany(x => x.Intervals).WithOne().HasForeignKey(x => x.OverrideId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(x => x.Slots).WithOne().HasForeignKey(x => x.OverrideId).OnDelete(DeleteBehavior.Cascade);
            entity.ToTable(table => table.HasCheckConstraint("CK_InspectionDateOverride_Mode", "\"Mode\" BETWEEN 0 AND 2"));
        });
        builder.Entity<InspectionDateInterval>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.ToTable(table => table.HasCheckConstraint("CK_InspectionDateInterval_Valid", "\"Start\" < \"End\""));
        });
        builder.Entity<InspectionSlotOverride>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.OverrideId, x.Start }).IsUnique();
            entity.ToTable(table => table.HasCheckConstraint("CK_InspectionSlotOverride_Mode", "\"Mode\" BETWEEN 0 AND 1"));
        });
        builder.Entity<RevokedSession>(entity =>
        {
            entity.HasKey(session => session.IdHash);
            entity.Property(session => session.IdHash).HasMaxLength(64);
            entity.Property(session => session.UserId).IsRequired();
            entity.HasIndex(session => session.ExpiresUtc);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(session => session.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(user => user.Name).HasMaxLength(200).IsRequired();
            entity.Property(user => user.PhoneNormalized).HasMaxLength(13).IsRequired();
            entity.Property(user => user.UserName).IsRequired();
            entity.Property(user => user.PhoneNumber).IsRequired();
            entity.HasIndex(user => user.PhoneNormalized).IsUnique();
            entity.ToTable(table => table.HasCheckConstraint("CK_Users_PhoneNormalized", "\"PhoneNormalized\" ~ '^\\+380[0-9]{9}$'"));
            entity.ToTable(table => table.HasCheckConstraint("CK_Users_PhoneIdentity", "\"UserName\" = \"PhoneNormalized\" AND \"PhoneNumber\" = \"PhoneNormalized\""));
            entity.ToTable(table => table.HasCheckConstraint("CK_Users_Name", "length(btrim(\"Name\")) > 0"));
        });
        builder.Entity<IdentityRole>(entity =>
        {
            entity.Property(role => role.Name).IsRequired();
            entity.Property(role => role.NormalizedName).IsRequired();
            entity.ToTable(table => table.HasCheckConstraint("CK_Roles_Allowed", "(\"Name\", \"NormalizedName\") IN (('User', 'USER'), ('Admin', 'ADMIN'), ('SuperAdmin', 'SUPERADMIN'))"));
            entity.HasData(
                new IdentityRole { Id = "role-user", Name = ApplicationRoles.User, NormalizedName = "USER", ConcurrencyStamp = "role-user" },
                new IdentityRole { Id = "role-admin", Name = ApplicationRoles.Admin, NormalizedName = "ADMIN", ConcurrencyStamp = "role-admin" },
                new IdentityRole { Id = "role-superadmin", Name = ApplicationRoles.SuperAdmin, NormalizedName = "SUPERADMIN", ConcurrencyStamp = "role-superadmin" });
        });
    }
}
