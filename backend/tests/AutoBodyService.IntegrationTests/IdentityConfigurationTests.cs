using AutoBodyService.Infrastructure;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AutoBodyService.IntegrationTests;

public sealed class IdentityConfigurationTests
{
    [Fact]
    public void IdentityCookieUsesSecurePersistentSessionSettings()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure("Host=localhost;Database=unused;Username=unused");
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        Assert.True(options.Cookie.HttpOnly);
        Assert.Equal(CookieSecurePolicy.Always, options.Cookie.SecurePolicy);
        Assert.Equal(SameSiteMode.Lax, options.Cookie.SameSite);
        Assert.Equal(TimeSpan.FromDays(7), options.ExpireTimeSpan);
        Assert.True(options.SlidingExpiration);
        Assert.Equal(TimeSpan.Zero, provider.GetRequiredService<IOptions<SecurityStampValidatorOptions>>().Value.ValidationInterval);
    }

    [Theory]
    [InlineData(null, "+380000000000", "Example-password-123!")]
    [InlineData("Test", "380000000000", "Example-password-123!")]
    [InlineData("Test", "+380000000000", null)]
    public async Task BootstrapRejectsInvalidConfigurationBeforeDatabaseAccess(string? name, string? phone, string? password)
    {
        var initializer = new SuperAdminInitializer(null!, null!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => initializer.InitializeAsync(name, phone, password));
    }

    [Fact]
    public void ModelSeedsOnlyApprovedRolesAndEnforcesUniquePhone()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused").Options;
        using var db = new ApplicationDbContext(options);
        var model = db.GetService<IDesignTimeModel>().Model;
        var roles = model.FindEntityType(typeof(IdentityRole))!.GetSeedData()
            .Select(row => (string)row[nameof(IdentityRole.Name)]!).OrderBy(name => name).ToArray();
        Assert.Equal(new[] { "Admin", "SuperAdmin", "User" }, roles);
        var user = model.FindEntityType(typeof(ApplicationUser))!;
        Assert.Contains(user.GetIndexes(), index => index.IsUnique && index.Properties.Count == 1 && index.Properties[0].Name == nameof(ApplicationUser.PhoneNormalized));
    }
}
