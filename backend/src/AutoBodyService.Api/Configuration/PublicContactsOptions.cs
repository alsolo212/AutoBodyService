namespace AutoBodyService.Api.Configuration;

/// <summary>Public contact details supplied by the deployment operator.</summary>
public sealed class PublicContactsOptions
{
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? WorkingHours { get; set; }
}
