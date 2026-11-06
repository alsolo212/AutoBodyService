using System.Text.RegularExpressions;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AutoBodyService.Infrastructure.Identity;

public sealed class SuperAdminInitializer(ApplicationDbContext db, UserManager<ApplicationUser> users)
{
    public async Task InitializeAsync(string? name, string? phone, string? password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200 ||
            phone is null || !Regex.IsMatch(phone, @"\A\+380[0-9]{9}\z") ||
            string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException("Initial administrator configuration is incomplete or invalid.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Serialize bootstrap and prevent concurrent inserts while checking that the installation is empty.
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"AspNetUsers\" IN EXCLUSIVE MODE", cancellationToken);
        if (await db.Users.AnyAsync(cancellationToken))
        {
            throw new InvalidOperationException("Initial administrator can only be created when there are no users.");
        }

        var user = new ApplicationUser
        {
            Name = name.Trim(),
            UserName = phone,
            PhoneNumber = phone,
            PhoneNormalized = phone,
        };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            throw new InvalidOperationException("Initial administrator could not be created; check the configured password policy.");
        }
        var assigned = await users.AddToRoleAsync(user, ApplicationRoles.SuperAdmin);
        if (!assigned.Succeeded)
        {
            throw new InvalidOperationException("Initial administrator role assignment failed.");
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
