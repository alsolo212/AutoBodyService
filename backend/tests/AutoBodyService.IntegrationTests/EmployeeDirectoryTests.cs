using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AutoBodyService.Domain.Employees;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.Employees;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AutoBodyService.IntegrationTests;

public sealed class EmployeeDomainTests
{
    [Fact]
    public void EmployeeValidatesTextAndLeavesExistingValuesUnchangedOnFailure()
    {
        var employee = new Employee(Guid.NewGuid(), "  Test Worker  ", " Bodywork ", true);
        Assert.Equal("Test Worker", employee.FullName);
        Assert.Equal("Bodywork", employee.Specialization);
        foreach (var invalid in new[] { "", " \t ", new string('x', 201), "Name\0" })
        {
            Assert.Throws<ArgumentException>(() => employee.Update(invalid, "Bodywork", false));
            Assert.Throws<ArgumentException>(() => employee.Update("Name", invalid, false));
            Assert.Equal("Test Worker", employee.FullName);
            Assert.Equal("Bodywork", employee.Specialization);
            Assert.True(employee.IsActive);
        }
        employee.Update(new string('a', 200), new string('b', 200), false);
        Assert.Equal(200, employee.FullName.Length);
        Assert.Equal(200, employee.Specialization.Length);
        Assert.False(employee.IsActive);
    }
}

[Collection("PostgresIdentity")]
public sealed class EmployeeDirectoryPostgresTests(PostgresIdentityFixture fixture) : IDisposable
{
    private readonly WebApplicationFactory<Program> factory = fixture.Factory.WithWebHostBuilder(_ => { });
    private const string Password = "Example-password-123!";
    private const string Path = "/api/admin/employees";
    public void Dispose() => factory.Dispose();

    private async Task<HttpClient> Login(string role)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var phone = "+380" + RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000);
        var user = new ApplicationUser { Name = "Test", UserName = phone, PhoneNumber = phone, PhoneNormalized = phone };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
        await Token(client);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/login", new { phone, password = Password })).StatusCode);
        await Token(client);
        return client;
    }
    private static async Task Token(HttpClient client)
    {
        using var response = await client.GetAsync("/api/auth/antiforgery");
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), s => s.StartsWith("XSRF-TOKEN="));
        client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        client.DefaultRequestHeaders.Add("RequestVerificationToken", Uri.UnescapeDataString(cookie.Split(';')[0]["XSRF-TOKEN=".Length..]));
    }

    [PostgresFact]
    public async Task AdminAndSuperAdminManageDirectoryAndInactiveWorkerPersistsWithoutIdentityAccount()
    {
        using var admin = await Login("Admin"); using var superAdmin = await Login("SuperAdmin");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userCount = await db.Users.CountAsync();
        using var superCreated = await superAdmin.PostAsJsonAsync(Path, new { fullName = "Second Worker", specialization = "Paint", isActive = true });
        Assert.Equal(HttpStatusCode.Created, superCreated.StatusCode);
        var second = await superCreated.Content.ReadFromJsonAsync<JsonElement>();
        var secondId = second.GetProperty("id").GetGuid();
        var secondVersion = second.GetProperty("version").GetUInt32();
        using var adminUpdate = await admin.PatchAsJsonAsync($"{Path}/{secondId}", new { version = secondVersion, specialization = "Refinishing" });
        Assert.Equal(HttpStatusCode.OK, adminUpdate.StatusCode);
        Assert.Equal("Refinishing", (await adminUpdate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("specialization").GetString());
        using var created = await admin.PostAsJsonAsync(Path, new { fullName = "  Directory Example  ", specialization = "  Repair  ", isActive = true });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var item = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = item.GetProperty("id").GetGuid();
        var version = item.GetProperty("version").GetUInt32();
        Assert.Equal("Directory Example", item.GetProperty("fullName").GetString());
        Assert.Equal("Repair", item.GetProperty("specialization").GetString());
        Assert.True(item.GetProperty("isActive").GetBoolean());
        Assert.Empty(item.GetProperty("currentAssignments").EnumerateArray());
        Assert.Equal($"{Path}/{id}", created.Headers.Location!.AbsolutePath);
        using var get = await superAdmin.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        using var updated = await superAdmin.PatchAsJsonAsync($"{Path}/{id}", new { version, fullName = "Updated", isActive = false });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var changed = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Updated", changed.GetProperty("fullName").GetString());
        Assert.Equal("Repair", changed.GetProperty("specialization").GetString());
        Assert.False(changed.GetProperty("isActive").GetBoolean());
        Assert.NotEqual(version, changed.GetProperty("version").GetUInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PatchAsJsonAsync($"{Path}/{id}", new { version, specialization = "Stale" })).StatusCode);
        using var list = await admin.GetAsync(Path);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var rows = await list.Content.ReadFromJsonAsync<JsonElement[]>();
        var row = Assert.Single(rows!, e => e.GetProperty("id").GetGuid() == id);
        Assert.False(row.GetProperty("isActive").GetBoolean());
        Assert.Equal("Updated", row.GetProperty("fullName").GetString());
        var stored = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == id);
        Assert.False(stored.IsActive);
        Assert.Equal("Repair", stored.Specialization);
        Assert.Equal(userCount, await db.Users.CountAsync());
        Assert.False(await db.Users.AnyAsync(u => u.Id == id.ToString()));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{Path}/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PatchAsJsonAsync($"{Path}/{Guid.NewGuid()}", new { version = 1, isActive = false })).StatusCode);
    }

    [PostgresFact]
    public async Task UserIsForbiddenAndInvalidRequestsCannotCreateOrChangeWorkers()
    {
        using var admin = await Login("Admin"); using var user = await Login("User");
        using var anonymous = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        using var created = await admin.PostAsJsonAsync(Path, new { fullName = "Validation", specialization = "Repair", isActive = true });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var item = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = item.GetProperty("id").GetGuid(); var version = item.GetProperty("version").GetUInt32();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync($"{Path}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync(Path, new { fullName = "No", specialization = "No", isActive = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PatchAsJsonAsync($"{Path}/{id}", new { version, isActive = false })).StatusCode);
        foreach (var body in new object[] {
            new { fullName = " ", specialization = "Repair", isActive = true },
            new { fullName = "Name", specialization = new string('x', 201), isActive = true },
            new { fullName = "Name", specialization = "Repair" },
            new { fullName = "Name", specialization = "Repair", isActive = true, password = "not-accepted" },
            new { fullName = "Name\0", specialization = "Repair", isActive = true } })
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Path, body)).StatusCode);
        foreach (var body in new object[] {
            new { version }, new { version, fullName = (string?)null }, new { version, specialization = "\t" },
            new { version, isActive = "false" }, new { version, fullName = new string('x', 201) },
            new { version, isActive = false, role = "Admin" }, new { isActive = false } })
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PatchAsJsonAsync($"{Path}/{id}", body)).StatusCode);
        var persisted = await (await admin.GetAsync($"{Path}/{id}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Validation", persisted.GetProperty("fullName").GetString());
        Assert.True(persisted.GetProperty("isActive").GetBoolean());
        Assert.Equal(version, persisted.GetProperty("version").GetUInt32());
    }

    [PostgresFact]
    public async Task DirectoryLoadsMultipleEmployeesWithTwoDatabaseQueries()
    {
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var seed = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            seed.Employees.AddRange(Enumerable.Range(0, 3).Select(i => new Employee(Guid.NewGuid(), $"Query worker {i}", "Repair", true)));
            await seed.SaveChangesAsync();
        }
        var counter = new ReadCounter();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("AUTOBODY_IDENTITY_POSTGRES"))
            .AddInterceptors(counter).Options;
        await using var db = new ApplicationDbContext(options);
        var employees = await new EmployeeDirectory(db).ListAsync(default);
        Assert.True(employees.Count >= 3);
        Assert.Equal(2, counter.Count);
    }

    private sealed class ReadCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [PostgresFact]
    public async Task DatabaseRejectsBlankWorkerFields()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"Employees\" (\"Id\", \"FullName\", \"Specialization\", \"IsActive\") VALUES ({Guid.NewGuid()}, {"  "}, {"Repair"}, {true})"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }
}
