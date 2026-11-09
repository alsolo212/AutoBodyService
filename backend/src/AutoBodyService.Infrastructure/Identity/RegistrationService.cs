using AutoBodyService.Application.Authentication;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AutoBodyService.Infrastructure.Identity;

public sealed class RegistrationService(ApplicationDbContext db, UserManager<ApplicationUser> users) : IRegistrationService
{
    public async Task<bool> RegisterAsync(string name, string phone, string password, CancellationToken cancellationToken)
    {
        var user = new ApplicationUser { Name = name, UserName = phone, PhoneNumber = phone, PhoneNormalized = phone };
        foreach (var validator in users.PasswordValidators)
        {
            if (!(await validator.ValidateAsync(users, user, password)).Succeeded) return false;
        }
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var created = await users.CreateAsync(user, password);
            if (!created.Succeeded)
            {
                // Do comparable password-hashing work for an already registered number.
                if (created.Errors.All(error => error.Code == users.ErrorDescriber.DuplicateUserName(phone).Code))
                {
                    _ = users.PasswordHasher.HashPassword(user, password);
                    return true;
                }
                return false;
            }
            var assigned = await users.AddToRoleAsync(user, ApplicationRoles.User);
            if (!assigned.Succeeded) throw new InvalidOperationException("The default user role could not be assigned.");
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_AspNetUsers_PhoneNormalized" or "UserNameIndex" })
        {
            return true;
        }
    }
}
