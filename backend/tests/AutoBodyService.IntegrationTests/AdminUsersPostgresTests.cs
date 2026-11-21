using System.Net;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.Persistence;
using AutoBodyService.Application.Audit;
using AutoBodyService.Application.Users;
using AutoBodyService.Application.InspectionSchedule;
using AutoBodyService.Domain.Appointments;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AutoBodyService.IntegrationTests;

[Collection("PostgresIdentity")]
public sealed class AdminUsersPostgresTests(PostgresIdentityFixture fixture) : IDisposable
{
    private readonly WebApplicationFactory<Program> factory = fixture.Factory is not null ? new() : throw new InvalidOperationException();
    private const string Password = "Example-password-123!";
    private HttpClient Client() => factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true });
    public void Dispose() => factory.Dispose();
    private async Task<ApplicationUser> Create(string role, bool adminOnly = false)
    {
        var phone = "+380" + RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000);
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { Name = "Manage " + Guid.NewGuid().ToString("N"), UserName = phone, PhoneNumber = phone, PhoneNormalized = phone };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role == ApplicationRoles.SuperAdmin || adminOnly ? role : ApplicationRoles.User)).Succeeded);
        if (role == ApplicationRoles.Admin && !adminOnly) Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return user;
    }
    private static async Task Token(HttpClient client)
    {
        using var response = await client.GetAsync("/api/auth/antiforgery");
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("XSRF-TOKEN="));
        client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        client.DefaultRequestHeaders.Add("RequestVerificationToken", Uri.UnescapeDataString(cookie.Split(';')[0]["XSRF-TOKEN=".Length..]));
    }
    private static async Task Login(HttpClient client, ApplicationUser user, string password = Password)
    {
        await Token(client);
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { phone = user.PhoneNormalized, password });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await Token(client);
    }
    private static async Task<JsonElement> Find(HttpClient client, ApplicationUser user)
    {
        using var response = await client.GetAsync("/api/admin/users?search=" + Uri.EscapeDataString(user.PhoneNormalized));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement[]>())!);
    }
    private static string Version(JsonElement user) => user.GetProperty("version").GetString()!;

    [PostgresFact]
    public async Task RoleMatrixRejectsHiddenTargetsTamperingAndMissingCsrf()
    {
        var super = await Create(ApplicationRoles.SuperAdmin); var admin = await Create(ApplicationRoles.Admin); var customer = await Create(ApplicationRoles.User);
        using var a = Client(); using var s = Client(); using var u = Client();
        await Login(a, admin); await Login(s, super); await Login(u, customer);
        Assert.Equal(HttpStatusCode.Forbidden, (await u.GetAsync("/api/admin/users")).StatusCode);
        Assert.Empty((await (await a.GetAsync("/api/admin/users?search=" + Uri.EscapeDataString(admin.PhoneNormalized))).Content.ReadFromJsonAsync<JsonElement[]>())!);
        Assert.Empty((await (await s.GetAsync("/api/admin/users?search=" + Uri.EscapeDataString(super.PhoneNormalized))).Content.ReadFromJsonAsync<JsonElement[]>())!);
        var row = await Find(a, customer); var adminRow = await Find(s, admin);
        Assert.Equal(new[] { "id", "isAdmin", "isBlocked", "name", "phone", "version" }, row.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(HttpStatusCode.NotFound, (await a.PostAsJsonAsync($"/api/admin/users/{admin.Id}/reset-password", new { version = Version(adminRow) })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.PutAsJsonAsync($"/api/admin/users/{customer.Id}/roles", new { version = Version(row), isAdmin = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.PatchAsJsonAsync($"/api/admin/users/{customer.Id}/block", new { version = Version(row), isBlocked = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.PutAsJsonAsync($"/api/admin/users/{customer.Id}/roles", new { version = Version(row), isAdmin = true, role = "SuperAdmin" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.PatchAsJsonAsync($"/api/admin/users/{super.Id}/block", new { version = super.ConcurrencyStamp, isBlocked = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.PutAsJsonAsync($"/api/admin/users/{super.Id}/roles", new { version = super.ConcurrencyStamp, isAdmin = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.PostAsJsonAsync($"/api/admin/users/{super.Id}/reset-password", new { version = super.ConcurrencyStamp })).StatusCode);
        s.DefaultRequestHeaders.Remove("RequestVerificationToken");
        Assert.Equal(HttpStatusCode.BadRequest, (await s.PatchAsJsonAsync($"/api/admin/users/{customer.Id}/block", new { version = Version(row), isBlocked = true })).StatusCode);
    }

    [PostgresFact]
    public async Task ResetRevokesCookieRequiresChangeAndPersistsNoSecret()
    {
        var logs = new CaptureLogs();
        factory.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);
        var super = await Create(ApplicationRoles.SuperAdmin); var admin = await Create(ApplicationRoles.Admin); var customer = await Create(ApplicationRoles.User);
        using var a = Client(); using var u = Client(); await Login(a, admin); await Login(u, customer);
        var row = await Find(a, customer);
        using var response = await a.PostAsJsonAsync($"/api/admin/users/{customer.Id}/reset-password", new { version = Version(row) });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(json.EnumerateObject());
        var temporary = json.GetProperty("temporaryPassword").GetString()!;
        Assert.True(temporary.Length >= 28);
        await using (var beforeScope = factory.Services.CreateAsyncScope())
        {
            var manager = beforeScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var resetUser = (await manager.FindByIdAsync(customer.Id))!;
            Assert.True(resetUser.MustChangePassword);
            Assert.NotEqual(temporary, resetUser.PasswordHash);
            Assert.True(await manager.CheckPasswordAsync(resetUser, temporary));
            Assert.False(await manager.CheckPasswordAsync(resetUser, Password));
        }
        var refreshed = await Find(a, customer);
        Assert.DoesNotContain(temporary, refreshed.GetRawText());
        Assert.Equal(HttpStatusCode.Unauthorized, (await u.GetAsync("/api/auth/session")).StatusCode);
        await Login(u, customer, temporary);
        Assert.Equal(HttpStatusCode.Forbidden, (await u.GetAsync("/api/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await u.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = temporary, newPassword = "Changed-password-123!" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await u.GetAsync("/api/profile")).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(); var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var stored = (await users.FindByIdAsync(customer.Id))!;
        Assert.NotEqual(temporary, stored.PasswordHash); Assert.False(await users.CheckPasswordAsync(stored, temporary));
        var audit = await db.AdminAuditEvents.Where(x => x.ObjectId == customer.Id && x.Action == "AccountPasswordReset").ToArrayAsync();
        Assert.Single(audit); Assert.Equal(admin.Id, audit[0].ActorId); Assert.DoesNotContain(temporary, audit[0].Details);
        using var supervisor = Client(); await Login(supervisor, super);
        var auditPage = await supervisor.GetFromJsonAsync<JsonElement>($"/api/admin/audit-log?actorId={admin.Id}&action=AccountPasswordReset");
        var entry = Assert.Single(auditPage.GetProperty("items").EnumerateArray());
        Assert.Equal(customer.Id, entry.GetProperty("objectId").GetString());
        Assert.DoesNotContain(temporary, auditPage.ToString());
        var notices = await db.Notifications.Where(x => x.Type == "AccountPasswordReset" && (x.RecipientId == customer.Id || x.RecipientId == super.Id)).ToArrayAsync();
        Assert.Equal(2, notices.Length); Assert.All(notices, n => { Assert.DoesNotContain(temporary, n.Title); Assert.DoesNotContain(temporary, n.Message); Assert.DoesNotContain(temporary, n.EventKey); });
        Assert.DoesNotContain(logs.Entries, entry => entry.Contains(temporary, StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task SuperAdminCanResetAndDemoteAdminWithOnlyAdminRole()
    {
        var super = await Create(ApplicationRoles.SuperAdmin); var admin = await Create(ApplicationRoles.Admin, adminOnly: true);
        using var s = Client(); using var a = Client(); await Login(s, super); await Login(a, admin);
        var row = await Find(s, admin); Assert.True(row.GetProperty("isAdmin").GetBoolean());
        using var reset = await s.PostAsJsonAsync($"/api/admin/users/{admin.Id}/reset-password", new { version = Version(row) });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await a.GetAsync("/api/admin/employees")).StatusCode);
        row = await Find(s, admin);
        Assert.Equal(HttpStatusCode.NoContent, (await s.PutAsJsonAsync($"/api/admin/users/{admin.Id}/roles", new { version = Version(row), isAdmin = false })).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.Equal(new[] { ApplicationRoles.User }, await manager.GetRolesAsync((await manager.FindByIdAsync(admin.Id))!));
    }

    [PostgresFact]
    public async Task SensitiveOperationsAreRateLimitedWithProblemDetails()
    {
        var super = await Create(ApplicationRoles.SuperAdmin); using var s = Client(); await Login(s, super);
        HttpResponseMessage? response = null;
        for (var i = 0; i < 12; i++)
        {
            response?.Dispose();
            response = await s.PatchAsJsonAsync("/api/admin/users/nonexistent/block", new { version = "v", isBlocked = true });
            if (response.StatusCode == HttpStatusCode.TooManyRequests) break;
        }
        using (response)
        {
            Assert.NotNull(response); Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }
    }

    [PostgresFact]
    public async Task AuditFailureRollsBackResetAndSecurityEvents()
    {
        var super = await Create(ApplicationRoles.SuperAdmin); var target = await Create(ApplicationRoles.User);
        using var failing = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Scoped<IAdminAuditWriter, FailingAudit>())));
        using var s = failing.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true });
        await Login(s, super); var row = await Find(s, target);
        using var response = await s.PostAsJsonAsync($"/api/admin/users/{target.Id}/reset-password", new { version = Version(row) });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await manager.FindByIdAsync(target.Id))!;
        Assert.False(user.MustChangePassword); Assert.True(await manager.CheckPasswordAsync(user, Password));
        Assert.Equal(target.SecurityStamp, user.SecurityStamp);
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.AdminAuditEvents.AnyAsync(e => e.ObjectId == target.Id));
        Assert.False(await db.Notifications.AnyAsync(n => n.RecipientId == target.Id && n.Type == "AccountPasswordReset"));
    }

    [PostgresFact]
    public async Task AdminChangesRevokeOldCookiesAndRespectStaleVersions()
    {
        var super = await Create(ApplicationRoles.SuperAdmin); var target = await Create(ApplicationRoles.Admin);
        using var s = Client(); using var t = Client(); await Login(s, super); await Login(t, target);
        var row = await Find(s, target); var old = Version(row);
        Assert.Equal(HttpStatusCode.NoContent, (await s.PutAsJsonAsync($"/api/admin/users/{target.Id}/roles", new { version = old, isAdmin = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await t.GetAsync("/api/admin/employees")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.PatchAsJsonAsync($"/api/admin/users/{target.Id}/block", new { version = old, isBlocked = true })).StatusCode);
        await Login(t, target);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.GetAsync("/api/admin/employees")).StatusCode);
        row = await Find(s, target);
        Assert.Equal(HttpStatusCode.NoContent, (await s.PatchAsJsonAsync($"/api/admin/users/{target.Id}/block", new { version = Version(row), isBlocked = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await t.GetAsync("/api/auth/session")).StatusCode);
        row = await Find(s, target);
        Assert.Equal(HttpStatusCode.NoContent, (await s.PatchAsJsonAsync($"/api/admin/users/{target.Id}/block", new { version = Version(row), isBlocked = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await t.GetAsync("/api/auth/session")).StatusCode);
        row = await Find(s, target);
        Assert.Equal(HttpStatusCode.NoContent, (await s.PutAsJsonAsync($"/api/admin/users/{target.Id}/roles", new { version = Version(row), isAdmin = true })).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.True(await manager.IsInRoleAsync((await manager.FindByIdAsync(target.Id))!, ApplicationRoles.Admin));
        var audit = await db.AdminAuditEvents.Where(e => e.ObjectId == target.Id).ToArrayAsync();
        Assert.Equal(4, audit.Length);
        Assert.Equal(2, audit.Count(e => e.Action == "AccountAdminChanged"));
        Assert.Equal(2, audit.Count(e => e.Action == "AccountBlockedChanged"));
        var notifications = await db.Notifications.Where(n => n.RecipientId == target.Id && (n.Type == "AccountAdminChanged" || n.Type == "AccountBlockedChanged")).ToArrayAsync();
        Assert.Equal(4, notifications.Length);
    }

    [PostgresFact]
    public async Task CompetingMutationsAllowOnlyOneVersionToCommit()
    {
        var super = await Create(ApplicationRoles.SuperAdmin); var target = await Create(ApplicationRoles.User);
        using var s = Client(); await Login(s, super); var row = await Find(s, target); var version = Version(row);
        var results = await Task.WhenAll(s.PatchAsJsonAsync($"/api/admin/users/{target.Id}/block", new { version, isBlocked = true }),
            s.PutAsJsonAsync($"/api/admin/users/{target.Id}/roles", new { version, isAdmin = true }));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await db.AdminAuditEvents.CountAsync(x => x.ObjectId == target.Id && (x.Action == "AccountAdminChanged" || x.Action == "AccountBlockedChanged")));
    }

    [PostgresFact]
    public async Task PreviouslyAuthorizedActorCannotResetAfterConcurrentDemotion()
    {
        var super = await Create(ApplicationRoles.SuperAdmin); var admin = await Create(ApplicationRoles.Admin); var target = await Create(ApplicationRoles.User);
        var staleActor = new AccountActor(admin.Id, admin.SecurityStamp!);
        using var s = Client(); await Login(s, super);
        var adminRow = await Find(s, admin); var targetRow = await Find(s, target);
        Assert.Equal(HttpStatusCode.NoContent, (await s.PutAsJsonAsync($"/api/admin/users/{admin.Id}/roles", new { version = Version(adminRow), isAdmin = false })).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAdminUsers>();
        var result = await service.ResetPasswordAsync(staleActor, target.Id, Version(targetRow), CancellationToken.None);
        Assert.Equal(AccountOperationResult.Forbidden, result.Result);
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.AdminAuditEvents.AnyAsync(e => e.ObjectId == target.Id && e.Action == "AccountPasswordReset"));
    }

    private sealed class FailingAudit : IAdminAuditWriter
    {
        public void RecordRepairCapacityChange(string actorId) => throw new InvalidOperationException("Audit failure");
        public void RecordRepairOfferDeclined(string actorId, Guid pauseId, Guid appointmentId) => throw new InvalidOperationException("Audit failure");
        public void RecordWorkshopContactChange(string actorId, bool phone) => throw new NotSupportedException();
        public void RecordAccountAction(string actorId, string targetId, string action, bool? value) => throw new InvalidOperationException("Audit unavailable");
        public void RecordExpenseChange(string actorId, Guid expenseId, decimal? beforeAmount, decimal afterAmount, bool beforeHasDescription, bool afterHasDescription, bool descriptionChanged) => throw new InvalidOperationException("Audit unavailable");
        public void RecordWeeklyScheduleChange(string actorId, IReadOnlyList<WeeklyInterval> before, IReadOnlyList<WeeklyInterval> after) => throw new NotSupportedException();
        public void RecordDateExceptionChange(string actorId, DateOnly date, InspectionException? before, InspectionException? after) => throw new NotSupportedException();
        public void RecordAppointmentStatusChange(string actorId, Guid appointmentId, AppointmentStatus from, AppointmentStatus to, Guid? statusEventId = null) => throw new NotSupportedException();
        public void RecordAssignmentChange(string actorId, Guid assignmentId, Guid appointmentId, Guid employeeId, bool released, bool automatic) => throw new NotSupportedException();
        public Task<AuditPage> ReadAsync(AuditQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void RecordRepairPlanChange(string actorId, Guid appointmentId, bool priceChanged, bool datesChanged) => throw new NotSupportedException();
        public void RecordPriceItemChange(string actorId, Guid priceItemId, bool created) => throw new NotSupportedException();
        public void RecordManualContact(string actorId, Guid appointmentId, ManualContactReason reason) => throw new NotSupportedException();
    }

    private sealed class CaptureLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Sink(Entries);
        public void Dispose() { }
        private sealed class Sink(ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(formatter(state, exception) + exception);
        }
    }
}
