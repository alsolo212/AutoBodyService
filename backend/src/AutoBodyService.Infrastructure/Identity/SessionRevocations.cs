using System.Security.Cryptography;
using System.Text;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AutoBodyService.Infrastructure.Identity;

public sealed class SessionRevocations(ApplicationDbContext db)
{
    public const string ClaimType = "autobody:session-id";
    private static string Hash(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
    public async Task<bool> IsRevokedAsync(string id, string userId)
    {
        var hash = Hash(id);
        return await db.RevokedSessions.AsNoTracking().AnyAsync(row => row.IdHash == hash && row.UserId == userId);
    }
    public async Task RevokeAsync(string id, string userId, DateTimeOffset expiresUtc)
    {
        var hash = Hash(id);
        // Cleanup is bounded by the expiry index. An inactive installation may retain expired rows until the next logout.
        if (db.Database.IsRelational()) await db.RevokedSessions.Where(row => row.ExpiresUtc <= DateTimeOffset.UtcNow).ExecuteDeleteAsync();
        else
        {
            var expired = await db.RevokedSessions.Where(row => row.ExpiresUtc <= DateTimeOffset.UtcNow).ToListAsync();
            db.RevokedSessions.RemoveRange(expired);
        }
        if (await db.RevokedSessions.AnyAsync(row => row.IdHash == hash)) return;
        db.RevokedSessions.Add(new RevokedSession { IdHash = hash, UserId = userId, ExpiresUtc = expiresUtc });
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "PK_RevokedSessions" })
        {
            // Concurrent logout of the same session already revoked it.
        }
    }
}
