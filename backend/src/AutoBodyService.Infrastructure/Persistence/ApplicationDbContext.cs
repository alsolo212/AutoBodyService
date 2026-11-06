using AutoBodyService.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AutoBodyService.Infrastructure.Persistence;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
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
