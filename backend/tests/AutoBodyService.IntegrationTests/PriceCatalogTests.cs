using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Domain.Prices;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AutoBodyService.IntegrationTests;

public sealed class PriceListItemDomainTests
{
    [Fact]
    public void PriceValidationAcceptsZeroAndPreservesStateWhenInvalid()
    {
        var item = new PriceListItem(Guid.NewGuid(), "  Test  ", " Description ", 0m, true, -1, true);
        Assert.Equal("Test", item.Name); Assert.Equal("Description", item.Description); Assert.Equal(0m, item.Price);
        Assert.True(item.IsPublished); Assert.False(item.IsArchived);
        Assert.Throws<ArgumentException>(() => item.Update("Test", "Description", -0.01m, false, 0, true, false));
        Assert.Throws<ArgumentException>(() => item.Update(" ", "Description", 0m, false, 0, true, false));
        Assert.Throws<ArgumentException>(() => item.Update("Test", new string('a', 1001), 0m, false, 0, true, false));
        Assert.Throws<ArgumentException>(() => item.Update("Test", "Description", 0m, false, 0, true, true));
        Assert.Equal(0m, item.Price); Assert.True(item.IsPublished); Assert.False(item.IsArchived);
        item.Update("Test", "", 12345678901234567890.12345678m, false, 2, false, true);
        Assert.Equal(12345678901234567890.12345678m, item.Price); Assert.True(item.IsArchived);
    }
}

[Collection("PostgresIdentity")]
public sealed class PriceCatalogPostgresTests(PostgresIdentityFixture fixture) : IDisposable
{
    private readonly WebApplicationFactory<Program> factory = fixture.Factory.WithWebHostBuilder(_ => { });
    private const string Password = "Example-password-123!";
    private const string AdminPath = "/api/admin/prices";
    private const string PublicPath = "/api/prices";
    public void Dispose() => factory.Dispose();

    private async Task<(string Id, string Phone)> User(string role, bool mustChangePassword = false)
    {
        await using var scope = factory.Services.CreateAsyncScope(); var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var phone = "+380" + RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000);
        var user = new ApplicationUser { Name = "Test", UserName = phone, PhoneNumber = phone, PhoneNormalized = phone, MustChangePassword = mustChangePassword };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded); Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return (user.Id, phone);
    }
    private HttpClient Client() => factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
    private static async Task Token(HttpClient client)
    {
        using var response = await client.GetAsync("/api/auth/antiforgery");
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), s => s.StartsWith("XSRF-TOKEN="));
        client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        client.DefaultRequestHeaders.Add("RequestVerificationToken", Uri.UnescapeDataString(cookie.Split(';')[0]["XSRF-TOKEN=".Length..]));
    }
    private async Task<HttpClient> Login(string phone)
    {
        var client = Client(); await Token(client);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/login", new { phone, password = Password })).StatusCode);
        await Token(client); return client;
    }
    private static async Task<JsonElement> Read(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();
    private static async Task<JsonElement> Add(HttpClient client, string name, decimal price, int order, bool published, bool isFrom = false)
    {
        using var response = await client.PostAsJsonAsync(AdminPath, new { name, description = "Тест", price, isFrom, sortOrder = order, isPublished = published });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var item = await Read(response); Assert.Equal($"{AdminPath}/{item.GetProperty("id").GetGuid()}", response.Headers.Location!.AbsolutePath);
        return item;
    }
    private static async Task<JsonElement> Change(HttpClient client, Guid id, object body)
    {
        using var response = await client.PatchAsJsonAsync($"{AdminPath}/{id}", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); return await Read(response);
    }

    [PostgresFact]
    public async Task PublicPricesAreOrderedExactAndRespectPublicationAndArchive()
    {
        var admin = await User("Admin"); var super = await User("SuperAdmin");
        using var actor = await Login(admin.Phone); using var supervisor = await Login(super.Phone); using var anonymous = Client();
        var key = Guid.NewGuid().ToString("N");
        var first = await Add(actor, key + "-first", 0m, 20, true, true);
        var second = await Add(supervisor, key + "-second", 12345678901234567890.12345678m, 10, true);
        var hidden = await Add(actor, key + "-hidden", 20m, 0, false);
        var firstId = first.GetProperty("id").GetGuid(); var secondId = second.GetProperty("id").GetGuid(); var hiddenId = hidden.GetProperty("id").GetGuid();
        var rows = (await anonymous.GetFromJsonAsync<JsonElement[]>(PublicPath))!.Where(x => x.GetProperty("name").GetString()!.StartsWith(key)).ToArray();
        Assert.Equal(new[] { secondId, firstId }, rows.Select(x => x.GetProperty("id").GetGuid()));
        Assert.Equal(0m, rows[1].GetProperty("price").GetDecimal()); Assert.True(rows[1].GetProperty("isFrom").GetBoolean());
        Assert.Equal(12345678901234567890.12345678m, rows[0].GetProperty("price").GetDecimal());
        Assert.Equal("12345678901234567890.12345678", rows[0].GetProperty("priceExact").GetString());
        Assert.Equal("12345678901234567890.12345678", second.GetProperty("priceExact").GetString());
        Assert.Equal("12345678901234567890.12345678", (await actor.GetFromJsonAsync<JsonElement>($"{AdminPath}/{secondId}")).GetProperty("priceExact").GetString());
        Assert.Equal("UAH", rows[0].GetProperty("currency").GetString());
        Assert.False(rows[0].TryGetProperty("version", out _)); Assert.False(rows[0].TryGetProperty("isArchived", out _));
        first = await Change(actor, firstId, new { version = first.GetProperty("version").GetUInt32(), isArchived = true });
        Assert.True(first.GetProperty("isArchived").GetBoolean()); Assert.False(first.GetProperty("isPublished").GetBoolean());
        var afterArchive = (await anonymous.GetFromJsonAsync<JsonElement[]>(PublicPath))!.Where(x => x.GetProperty("name").GetString()!.StartsWith(key)).ToArray();
        Assert.Equal(secondId, Assert.Single(afterArchive).GetProperty("id").GetGuid());
        Assert.True((await actor.GetFromJsonAsync<JsonElement>($"{AdminPath}/{firstId}")).GetProperty("isArchived").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await actor.PatchAsJsonAsync($"{AdminPath}/{firstId}", new { version = first.GetProperty("version").GetUInt32(), isPublished = true })).StatusCode);
        first = await Change(actor, firstId, new { version = first.GetProperty("version").GetUInt32(), isArchived = false, isPublished = true, sortOrder = -10 });
        second = await Change(supervisor, secondId, new { version = second.GetProperty("version").GetUInt32(), isPublished = false });
        hidden = await Change(actor, hiddenId, new { version = hidden.GetProperty("version").GetUInt32(), isPublished = true, sortOrder = 5, price = 0m });
        rows = (await anonymous.GetFromJsonAsync<JsonElement[]>(PublicPath))!.Where(x => x.GetProperty("name").GetString()!.StartsWith(key)).ToArray();
        Assert.Equal(new[] { firstId, hiddenId }, rows.Select(x => x.GetProperty("id").GetGuid()));
        Assert.Equal(0m, rows[1].GetProperty("price").GetDecimal());
        var all = (await actor.GetFromJsonAsync<JsonElement[]>(AdminPath))!.Where(x => x.GetProperty("name").GetString()!.StartsWith(key)).ToArray();
        Assert.Equal(3, all.Length); Assert.False(Assert.Single(all, x => x.GetProperty("id").GetGuid() == secondId).GetProperty("isPublished").GetBoolean());
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(3, await db.PriceListItems.CountAsync(x => x.Id == firstId || x.Id == secondId || x.Id == hiddenId));
        var audit = await db.AdminAuditEvents.Where(x => x.ObjectId == firstId.ToString()).ToListAsync();
        Assert.Equal(3, audit.Count); Assert.Single(audit, x => x.Action == "PriceItemCreated"); Assert.Equal(2, audit.Count(x => x.Action == "PriceItemChanged"));
        Assert.All(audit, x => { Assert.Equal(admin.Id, x.ActorId); Assert.Equal("{}", x.Details); });
    }

    [PostgresFact]
    public async Task RolesCsrfAndValidationProtectWritesWhilePublicGetIsAnonymous()
    {
        var admin = await User("Admin"); var user = await User("User"); var temporary = await User("User", true);
        using var actor = await Login(admin.Phone); using var customer = await Login(user.Phone); using var restricted = await Login(temporary.Phone); using var anonymous = Client();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(PublicPath)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync(PublicPath)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await restricted.GetAsync(PublicPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(AdminPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(AdminPath, new { name = "Test", price = 1, isFrom = false, sortOrder = 1, isPublished = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync(AdminPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsJsonAsync(AdminPath, new { name = "Test", price = 1, isFrom = false, sortOrder = 1, isPublished = true })).StatusCode);
        var item = await Add(actor, "Validation", 0m, 0, true); var id = item.GetProperty("id").GetGuid(); var version = item.GetProperty("version").GetUInt32();
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PatchAsJsonAsync($"{AdminPath}/{id}", new { version, price = 3 })).StatusCode);
        foreach (var body in new object[] {
            new { name = " ", price = 1, isFrom = false, sortOrder = 0, isPublished = true },
            new { name = "Test", price = -0.01m, isFrom = false, sortOrder = 0, isPublished = true },
            new { name = new string('x', 201), price = 1, isFrom = false, sortOrder = 0, isPublished = true },
            new { name = "Test", description = new string('x', 1001), price = 1, isFrom = false, sortOrder = 0, isPublished = true },
            new { name = "Test", price = 1, isFrom = false, sortOrder = 0 },
            new { name = "Test", price = 1, isFrom = false, sortOrder = 0, isPublished = true, currency = "USD" } })
            Assert.Equal(HttpStatusCode.BadRequest, (await actor.PostAsJsonAsync(AdminPath, body)).StatusCode);
        foreach (var body in new object[] { new { version }, new { version, price = -1 }, new { version, price = (decimal?)null },
            new { version, sortOrder = 1.5 }, new { version, name = "\t" }, new { version, isArchived = true, isPublished = true }, new { version, currency = "USD" }, new { price = 1 } })
            Assert.Equal(HttpStatusCode.BadRequest, (await actor.PatchAsJsonAsync($"{AdminPath}/{id}", body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await actor.PatchAsJsonAsync($"{AdminPath}/{Guid.NewGuid()}", new { version, price = 1 })).StatusCode);
        actor.DefaultRequestHeaders.Remove("RequestVerificationToken");
        Assert.Equal(HttpStatusCode.BadRequest, (await actor.PostAsJsonAsync(AdminPath, new { name = "Test", price = 1, isFrom = false, sortOrder = 0, isPublished = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await actor.PatchAsJsonAsync($"{AdminPath}/{id}", new { version, price = 1 })).StatusCode);
        var stored = await actor.GetFromJsonAsync<JsonElement>($"{AdminPath}/{id}");
        Assert.Equal(0m, stored.GetProperty("price").GetDecimal()); Assert.Equal(version, stored.GetProperty("version").GetUInt32());
    }

    [PostgresFact]
    public async Task ConcurrentPriceUpdatesRejectStaleVersionAndAuditOnlyCommittedChange()
    {
        var admin = await User("Admin"); var second = await User("SuperAdmin");
        using var first = await Login(admin.Phone); using var other = await Login(second.Phone);
        var item = await Add(first, "Concurrency", 1m, 0, true); var id = item.GetProperty("id").GetGuid(); var version = item.GetProperty("version").GetUInt32();
        var responses = await Task.WhenAll(first.PatchAsJsonAsync($"{AdminPath}/{id}", new { version, price = 2m }), other.PatchAsJsonAsync($"{AdminPath}/{id}", new { version, price = 3m }));
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }.Order(), responses.Select(x => x.StatusCode).Order());
        var winner = await Read(responses.Single(x => x.StatusCode == HttpStatusCode.OK));
        var stored = await first.GetFromJsonAsync<JsonElement>($"{AdminPath}/{id}");
        Assert.Equal(winner.GetProperty("price").GetDecimal(), stored.GetProperty("price").GetDecimal());
        Assert.NotEqual(version, stored.GetProperty("version").GetUInt32());
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var audit = await db.AdminAuditEvents.Where(x => x.ObjectId == id.ToString()).ToListAsync();
        Assert.Single(audit, x => x.Action == "PriceItemCreated"); Assert.Single(audit, x => x.Action == "PriceItemChanged");
        foreach (var response in responses) response.Dispose();
    }

    [PostgresFact]
    public async Task DatabaseRejectsNegativeNonFiniteAndArchivedPublishedPrices()
    {
        var admin = await User("Admin"); using var actor = await Login(admin.Phone);
        var item = await Add(actor, "Constraints", 0m, 0, true); var id = item.GetProperty("id").GetGuid();
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.OpenConnectionAsync(); var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        foreach (var sql in new[] {
            "UPDATE \"PriceListItems\" SET \"Price\" = -0.01 WHERE \"Id\" = @id",
            "UPDATE \"PriceListItems\" SET \"Price\" = 'NaN'::numeric WHERE \"Id\" = @id",
            "UPDATE \"PriceListItems\" SET \"Price\" = 'Infinity'::numeric WHERE \"Id\" = @id",
            "UPDATE \"PriceListItems\" SET \"IsArchived\" = TRUE WHERE \"Id\" = @id",
            "UPDATE \"PriceListItems\" SET \"Name\" = '   ' WHERE \"Id\" = @id" })
        {
            await using var command = new NpgsqlCommand(sql, connection); command.Parameters.AddWithValue("id", id);
            var error = await Assert.ThrowsAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
        var stored = await db.PriceListItems.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(0m, stored.Price); Assert.True(stored.IsPublished); Assert.False(stored.IsArchived);
    }

    [PostgresFact]
    public async Task ChangingPublicPriceNeverChangesExistingAgreedRepairPlan()
    {
        var admin = await User("Admin"); var owner = await User("User"); using var actor = await Login(admin.Phone); using var customer = await Login(owner.Phone);
        var moment = DateTimeOffset.UtcNow; var id = Guid.NewGuid(); var slot = new DateTimeOffset(2099, 3, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(RandomNumberGenerator.GetInt32(1, 1_000_000));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var appointment = new Appointment(id, owner.Id, slot, "1HGCM82633A123456", "Test", "Car", "Issue", moment);
            appointment.Schedule(new RepairPlan(id, 1250.35m, slot, slot.AddDays(2), moment), true);
            db.Appointments.Add(appointment); await db.SaveChangesAsync();
        }
        var item = await Add(actor, "Repair estimate", 1250.35m, 0, true); var priceId = item.GetProperty("id").GetGuid();
        var before = await customer.GetFromJsonAsync<JsonElement>($"/api/appointments/{id}");
        var planBefore = before.GetProperty("repairPlan");
        Assert.Equal(1250.35m, planBefore.GetProperty("agreedPrice").GetDecimal());
        item = await Change(actor, priceId, new { version = item.GetProperty("version").GetUInt32(), price = 9999.99m, isFrom = true });
        Assert.Equal(9999.99m, item.GetProperty("price").GetDecimal());
        var after = await customer.GetFromJsonAsync<JsonElement>($"/api/appointments/{id}");
        var planAfter = after.GetProperty("repairPlan");
        Assert.Equal(planBefore.GetProperty("agreedPrice").GetDecimal(), planAfter.GetProperty("agreedPrice").GetDecimal());
        Assert.Equal(planBefore.GetProperty("currency").GetString(), planAfter.GetProperty("currency").GetString());
        Assert.Equal(planBefore.GetProperty("plannedStartUtc").GetDateTimeOffset(), planAfter.GetProperty("plannedStartUtc").GetDateTimeOffset());
        Assert.Equal(planBefore.GetProperty("estimatedFinishUtc").GetDateTimeOffset(), planAfter.GetProperty("estimatedFinishUtc").GetDateTimeOffset());
        await using var verify = factory.Services.CreateAsyncScope(); var verifyDb = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persisted = await verifyDb.Set<RepairPlan>().AsNoTracking().SingleAsync(x => x.AppointmentId == id);
        Assert.Equal(1250.35m, persisted.AgreedPrice); Assert.Equal("UAH", persisted.Currency);
    }

    [PostgresFact]
    public async Task AuditFailureRollsBackPriceCreationAndUpdate()
    {
        var admin = await User("Admin"); using var actor = await Login(admin.Phone);
        var item = await Add(actor, "Audit original", 5m, 0, true); var id = item.GetProperty("id").GetGuid(); var version = item.GetProperty("version").GetUInt32();
        var unique = "Rollback " + Guid.NewGuid().ToString("N");
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_price_audit_test() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW."Action" IN ('PriceItemCreated', 'PriceItemChanged') THEN RAISE EXCEPTION 'test failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER reject_price_audit_test BEFORE INSERT ON "AdminAuditEvents" FOR EACH ROW EXECUTE FUNCTION reject_price_audit_test();
            """);
        try
        {
            Assert.Equal(HttpStatusCode.InternalServerError, (await actor.PostAsJsonAsync(AdminPath, new { name = unique, price = 5, isFrom = false, sortOrder = 0, isPublished = true })).StatusCode);
            Assert.Equal(HttpStatusCode.InternalServerError, (await actor.PatchAsJsonAsync($"{AdminPath}/{id}", new { version, price = 9 })).StatusCode);
            Assert.False(await db.PriceListItems.AsNoTracking().AnyAsync(x => x.Name == unique));
            var saved = await db.PriceListItems.AsNoTracking().SingleAsync(x => x.Id == id);
            Assert.Equal(5m, saved.Price);
            Assert.Single(await db.AdminAuditEvents.Where(x => x.ObjectId == id.ToString()).ToListAsync());
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_price_audit_test ON \"AdminAuditEvents\"; DROP FUNCTION reject_price_audit_test();"); }
    }
}
