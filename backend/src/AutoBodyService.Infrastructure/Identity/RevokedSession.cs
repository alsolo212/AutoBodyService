namespace AutoBodyService.Infrastructure.Identity;

public sealed class RevokedSession
{
    public string IdHash { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public DateTimeOffset ExpiresUtc { get; set; }
}
