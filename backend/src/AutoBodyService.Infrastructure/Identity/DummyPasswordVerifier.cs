using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace AutoBodyService.Infrastructure.Identity;

/// <summary>Per-process dummy hash used to keep unknown-account password checks comparable.</summary>
public sealed class DummyPasswordVerifier
{
    private readonly ApplicationUser user = new();
    private readonly string passwordHash;

    public DummyPasswordVerifier(IServiceScopeFactory scopes)
    {
        using var scope = scopes.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<ApplicationUser>>();
        var randomPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        passwordHash = hasher.HashPassword(user, randomPassword);
    }

    public void Verify(IPasswordHasher<ApplicationUser> hasher, string password) =>
        _ = hasher.VerifyHashedPassword(user, passwordHash, password);
}
