using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AutoBodyService.IntegrationTests;

public sealed class AuthApiTests
{
    private static WebApplicationFactory<Program> CreateFactory()
    {
        var databaseName = Guid.NewGuid().ToString();
        var postgres = Environment.GetEnvironmentVariable("AUTH_TEST_POSTGRES");
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();
            services.AddDbContext<ApplicationDbContext>(options =>
            {
                if (postgres is not null) options.UseNpgsql(postgres);
                else options.UseInMemoryDatabase(databaseName).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
            });
        }));
    }

    private static async Task SetToken(HttpClient client)
    {
        using var response = await client.GetAsync("/api/auth/antiforgery");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("XSRF-TOKEN="));
        client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        client.DefaultRequestHeaders.Add("RequestVerificationToken", Uri.UnescapeDataString(cookie.Split(';')[0]["XSRF-TOKEN=".Length..]));
    }

    [Fact]
    public async Task RegistrationLoginSessionLogoutEnforceSecurity()
    {
        using var factory = CreateFactory();
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreatedAsync();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
        var registration = new { name = "  Test customer  ", phone = "(000) 000-00-00", password = "Example-password-123!", role = "SuperAdmin" };
        using var noToken = await client.PostAsJsonAsync("/api/auth/register", registration);
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        await SetToken(client);
        using var created = await client.PostAsJsonAsync("/api/auth/register", registration);
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        Assert.Null(created.Headers.Location);
        using (var scope = factory.Services.CreateScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await manager.FindByNameAsync("+380000000000");
            Assert.NotNull(user);
            Assert.Equal("Test customer", user.Name);
            Assert.NotEqual("Example-password-123!", user.PasswordHash);
            Assert.True(await manager.CheckPasswordAsync(user, "Example-password-123!"));
            Assert.Equal(new[] { "User" }, await manager.GetRolesAsync(user));
        }
        using var duplicate = await client.PostAsJsonAsync("/api/auth/register", new { name = "Test", phone = "380000000000", password = "Example-password-123!" });
        using var weak = await client.PostAsJsonAsync("/api/auth/register", new { name = "Test", phone = "0000000001", password = "weak" });
        Assert.Equal(HttpStatusCode.Accepted, duplicate.StatusCode);
        Assert.Equal(await created.Content.ReadAsStringAsync(), await duplicate.Content.ReadAsStringAsync());
        Assert.Equal(created.Content.Headers.ContentType, duplicate.Content.Headers.ContentType);
        Assert.Equal(created.Headers.Location, duplicate.Headers.Location);
        using (var scope = factory.Services.CreateScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await manager.FindByNameAsync("+380000000000"))!;
            Assert.Equal("Test customer", user.Name);
            Assert.True(await manager.CheckPasswordAsync(user, "Example-password-123!"));
            Assert.Equal(new[] { "User" }, await manager.GetRolesAsync(user));
        }
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        using var weakDuplicate = await client.PostAsJsonAsync("/api/auth/register", new { name = "Test", phone = "0000000000", password = "weak" });
        Assert.Equal(HttpStatusCode.BadRequest, weakDuplicate.StatusCode);
        Assert.Equal((await weak.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString(),
            (await weakDuplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        using var wrong = await client.PostAsJsonAsync("/api/auth/login", new { phone = "+380000000000", password = "Incorrect-password-123!" });
        using var unknown = await client.PostAsJsonAsync("/api/auth/login", new { phone = "+380000000001", password = "Incorrect-password-123!" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        var wrongBody = await wrong.Content.ReadFromJsonAsync<JsonElement>();
        var unknownBody = await unknown.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(wrongBody.GetProperty("title").GetString(), unknownBody.GetProperty("title").GetString());
        Assert.Equal(wrongBody.EnumerateObject().Select(p => p.Name).OrderBy(x => x), unknownBody.EnumerateObject().Select(p => p.Name).OrderBy(x => x));
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { phone = "000-000-00-00", password = "Example-password-123!" });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        using var session = await client.GetAsync("/api/auth/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        var data = await session.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "mustChangePassword", "name", "phone", "roles" }, data.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToArray());
        Assert.Equal("+380000000000", data.GetProperty("phone").GetString());
        Assert.False(data.GetProperty("mustChangePassword").GetBoolean());
        Assert.Equal("User", Assert.Single(data.GetProperty("roles").EnumerateArray()).GetString());
        using (var scope = factory.Services.CreateScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await manager.FindByNameAsync("+380000000000"))!;
            user.MustChangePassword = true;
            Assert.True((await manager.UpdateAsync(user)).Succeeded);
        }
        using var temporarySession = await client.GetAsync("/api/auth/session");
        Assert.True((await temporarySession.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mustChangePassword").GetBoolean());
        using var staleTokenLogout = await client.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.BadRequest, staleTokenLogout.StatusCode);
        await SetToken(client);
        using var logout = await client.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var afterLogout = await client.GetAsync("/api/auth/session");
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    [Theory]
    [InlineData("", "+380000000000")]
    [InlineData("Test", "+38000000000")]
    [InlineData("Test", "+380000000000x")]
    [InlineData("Test", "+38000000000+0")]
    public async Task RegistrationRejectsInvalidInput(string name, string phone)
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        await SetToken(client);
        using var response = await client.PostAsJsonAsync("/api/auth/register", new { name, phone, password = "Example-password-123!" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RegistrationIsRateLimited()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        for (var i = 0; i < 10; i++)
        {
            using var response = await client.PostAsync("/api/auth/register", null);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var limited = await client.PostAsync("/api/auth/register", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }
}
