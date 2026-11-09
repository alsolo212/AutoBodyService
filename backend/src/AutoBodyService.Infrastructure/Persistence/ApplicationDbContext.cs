using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Domain.InspectionSchedule;
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

    public DbSet<RevokedSession> RevokedSessions => Set<RevokedSession>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
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
        var roleUserIds = ChangedRoleUserIds();
        var users = roleUserIds.Length == 0 ? [] : await Users.Where(user => roleUserIds.Contains(user.Id)).ToListAsync(cancellationToken);
        var blockedIds = ModifiedUserIds();
        var previousBlocks = blockedIds.Length == 0 ? [] : await Users.AsNoTracking()
            .Where(user => blockedIds.Contains(user.Id)).Select(user => new { user.Id, user.IsBlocked }).ToListAsync(cancellationToken);
        RevokeChangedSessions(users, previousBlocks.ToDictionary(user => user.Id, user => user.IsBlocked));
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
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
