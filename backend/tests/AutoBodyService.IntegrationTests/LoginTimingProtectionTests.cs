using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AutoBodyService.IntegrationTests;

public sealed class LoginTimingProtectionTests
{
    [Fact]
    public async Task UnknownAndWrongPasswordUseIdentityVerificationWithoutRehashingPerRequest()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();
            services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase("timing-" + services.GetHashCode()));
            services.RemoveAll<IPasswordHasher<ApplicationUser>>();
            services.AddSingleton<HashProbe>();
            services.AddScoped<IPasswordHasher<ApplicationUser>, ObservedIdentityHasher>();
        }));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.EnsureCreatedAsync();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { Name = "Test", UserName = "+380000000000", PhoneNumber = "+380000000000", PhoneNormalized = "+380000000000" };
            Assert.True((await users.CreateAsync(user, "Example-password-123!")).Succeeded);
        }
        var probe = factory.Services.GetRequiredService<HashProbe>();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        using var csrf = await client.GetAsync("/api/auth/antiforgery");
        var cookie = Assert.Single(csrf.Headers.GetValues("Set-Cookie"), value => value.StartsWith("XSRF-TOKEN="));
        client.DefaultRequestHeaders.Add("RequestVerificationToken", Uri.UnescapeDataString(cookie.Split(';')[0]["XSRF-TOKEN=".Length..]));
        var initialHashes = probe.Hashes;
        var initialChecks = probe.Checks;
        using var unknown = await client.PostAsJsonAsync("/api/auth/login", new { phone = "+380000000001", password = "Incorrect-password-123!" });
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(initialChecks + 1, probe.Checks);
        using var unknownAgain = await client.PostAsJsonAsync("/api/auth/login", new { phone = "+380000000002", password = "Incorrect-password-123!" });
        Assert.Equal(HttpStatusCode.Unauthorized, unknownAgain.StatusCode);
        Assert.Equal(initialChecks + 2, probe.Checks);
        Assert.Equal(initialHashes, probe.Hashes);
        using var wrong = await client.PostAsJsonAsync("/api/auth/login", new { phone = "+380000000000", password = "Incorrect-password-123!" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(initialChecks + 3, probe.Checks);
        Assert.Equal(initialHashes, probe.Hashes);
        var unknownBody = await unknown.Content.ReadFromJsonAsync<JsonElement>();
        var wrongBody = await wrong.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(unknownBody.EnumerateObject().Select(p => p.Name).OrderBy(name => name), wrongBody.EnumerateObject().Select(p => p.Name).OrderBy(name => name));
        foreach (var key in new[] { "type", "title", "status" })
            Assert.Equal(unknownBody.GetProperty(key).ToString(), wrongBody.GetProperty(key).ToString());
    }

    private sealed class HashProbe
    {
        private int hashes; private int checks;
        public int Hashes => Volatile.Read(ref hashes);
        public int Checks => Volatile.Read(ref checks);
        public void Hashed() => Interlocked.Increment(ref hashes);
        public void Checked() => Interlocked.Increment(ref checks);
    }
    private sealed class ObservedIdentityHasher(HashProbe probe) : IPasswordHasher<ApplicationUser>
    {
        private readonly PasswordHasher<ApplicationUser> inner = new();
        public string HashPassword(ApplicationUser user, string password) { probe.Hashed(); return inner.HashPassword(user, password); }
        public PasswordVerificationResult VerifyHashedPassword(ApplicationUser user, string hashedPassword, string providedPassword)
        { probe.Checked(); return inner.VerifyHashedPassword(user, hashedPassword, providedPassword); }
    }
}
