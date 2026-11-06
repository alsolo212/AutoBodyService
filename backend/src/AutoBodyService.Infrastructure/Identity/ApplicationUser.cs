using Microsoft.AspNetCore.Identity;

namespace AutoBodyService.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser
{
    public string Name { get; set; } = string.Empty;
    public string PhoneNormalized { get; set; } = string.Empty;
    public bool MustChangePassword { get; set; }
    public bool IsBlocked { get; set; }
}
