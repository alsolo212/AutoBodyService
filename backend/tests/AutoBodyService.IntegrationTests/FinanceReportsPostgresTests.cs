using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoBodyService.Application.Audit;
using AutoBodyService.Application.InspectionSchedule;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.Audit;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace AutoBodyService.IntegrationTests;

[Collection("PostgresIdentity")]
public sealed class FinanceReportsPostgresTests : IDisposable
{
    private const string Password = "Example-password-123!";
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2072, 9, 30, 20, 59, 59, TimeSpan.Zero));
    private readonly WebApplicationFactory<Program> factory;
    public FinanceReportsPostgresTests(PostgresIdentityFixture fixture)
    {
        factory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock);
        }));
    }
    public void Dispose() => factory.Dispose();
    private HttpClient Client() => factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
    private async Task<ApplicationUser> CreateUser(string role)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var phone = "+380" + RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000);
        var user = new ApplicationUser { Name = "Test", UserName = phone, PhoneNumber = phone, PhoneNormalized = phone };
        Assert.True((await manager.CreateAsync(user, Password)).Succeeded);
        Assert.True((await manager.AddToRoleAsync(user, role)).Succeeded);
        return user;
    }
    private static async Task Token(HttpClient client)
    {
        using var response = await client.GetAsync("/api/auth/antiforgery");
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), x => x.StartsWith("XSRF-TOKEN="));
        client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        client.DefaultRequestHeaders.Add("RequestVerificationToken", Uri.UnescapeDataString(cookie.Split(';')[0]["XSRF-TOKEN=".Length..]));
    }
    private async Task<HttpClient> Login(ApplicationUser user)
    {
        var client = Client(); await Token(client);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/login", new { phone = user.PhoneNormalized, password = Password })).StatusCode);
        await Token(client); return client;
    }
    private async Task<Guid> Seed(string ownerId, DateTimeOffset time, decimal amount, AppointmentStatus status = AppointmentStatus.Completed,
        bool confirmation = true, bool statusEvent = true, bool duplicate = false, DateTimeOffset? confirmationTime = null)
    {
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var slot = new DateTimeOffset(2090, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(RandomNumberGenerator.GetInt32(1, 100_000_000));
        var appointment = new Appointment(Guid.NewGuid(), ownerId, slot, "1HGCM82633A123456", "Test", "Model", "Test issue", new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        appointment.Schedule(new RepairPlan(appointment.Id, amount, slot, slot.AddHours(1), new DateTimeOffset(2020, 1, 2, 0, 0, 0, TimeSpan.Zero)), true);
        appointment.StartRepair();
        if (status == AppointmentStatus.Cancelled) appointment.CancelByAdmin(true, "Test cancellation");
        else if (status != AppointmentStatus.InProgress) appointment.FinishRepair();
        if (status == AppointmentStatus.Completed) appointment.Complete(true, true);
        db.Appointments.Add(appointment);
        if (confirmation) db.PaymentHandoverConfirmations.Add(new PaymentHandoverConfirmation(appointment.Id, ownerId, confirmationTime ?? time));
        if (statusEvent) db.AppointmentStatusEvents.Add(AppointmentStatusEvent.ForTransition(appointment.Id, AppointmentStatus.AwaitingPayment, AppointmentStatus.Completed, ownerId, time));
        if (duplicate) db.AppointmentStatusEvents.Add(AppointmentStatusEvent.ForTransition(appointment.Id, AppointmentStatus.AwaitingPayment, AppointmentStatus.Completed, ownerId, time.AddSeconds(1)));
        await db.SaveChangesAsync(); return appointment.Id;
    }
    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    private static decimal Number(JsonElement json, string name) => json.GetProperty(name).GetDecimal();

    [PostgresFact]
    public async Task ReportUsesKyivMonthCompletionAndCountsEachEligibleOrderOnce()
    {
        var super = await CreateUser("SuperAdmin"); var user = await CreateUser("User");
        var before = new DateTimeOffset(2071, 9, 30, 20, 59, 59, TimeSpan.Zero);
        var after = before.AddSeconds(1);
        var september = await Seed(user.Id, before, 10.25m);
        var october = await Seed(user.Id, after, 20.10m, duplicate: true);
        await Seed(user.Id, after.AddMinutes(1), 999m, AppointmentStatus.AwaitingPayment);
        await Seed(user.Id, after.AddMinutes(2), 999m, AppointmentStatus.InProgress, confirmation: false, statusEvent: false);
        await Seed(user.Id, after.AddMinutes(5), 999m, AppointmentStatus.Cancelled);
        await Seed(user.Id, after.AddMinutes(3), 999m, confirmation: false);
        await Seed(user.Id, after.AddMinutes(4), 999m, statusEvent: false);
        await Seed(user.Id, after.AddMinutes(6), 999m, confirmationTime: after.AddMinutes(7));
        var latePayment = await Seed(user.Id, after.AddMinutes(8), 999m);
        var lateHandover = await Seed(user.Id, after.AddMinutes(9), 999m);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var later = after.AddMinutes(10);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"PaymentHandoverConfirmations\" SET \"PaidAtUtc\" = {later} WHERE \"AppointmentId\" = {latePayment}");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"PaymentHandoverConfirmations\" SET \"HandedOverAtUtc\" = {later} WHERE \"AppointmentId\" = {lateHandover}");
        }
        using var client = await Login(super);
        var sep = await Json(await client.GetAsync("/api/admin/reports/finance?year=2071&month=9"));
        Assert.Equal(1, sep.GetProperty("completedCount").GetInt32()); Assert.Equal(10.25m, Number(sep, "revenue"));
        Assert.Equal(0m, Number(sep, "expenses")); Assert.Equal(10.25m, Number(sep, "result"));
        Assert.Equal(september, sep.GetProperty("orders")[0].GetProperty("id").GetGuid());
        var oct = await Json(await client.GetAsync("/api/admin/reports/finance?year=2071&month=10"));
        Assert.Equal(1, oct.GetProperty("completedCount").GetInt32()); Assert.Equal(20.10m, Number(oct, "revenue"));
        Assert.Equal(october, oct.GetProperty("orders")[0].GetProperty("id").GetGuid());
        Assert.Equal("UAH", oct.GetProperty("currency").GetString());
        var empty = await Json(await client.GetAsync("/api/admin/reports/finance?year=2071&month=11"));
        Assert.Equal(0, empty.GetProperty("completedCount").GetInt32()); Assert.Equal(0m, Number(empty, "revenue"));
        Assert.Equal(0m, Number(empty, "expenses")); Assert.Equal(0m, Number(empty, "result")); Assert.Empty(empty.GetProperty("orders").EnumerateArray());
    }

    [PostgresFact]
    public async Task ReportIncludesBothInstantsAcrossKyivDstChangesAndSortsPages()
    {
        var super = await CreateUser("SuperAdmin"); var user = await CreateUser("User");
        // 2044: spring offset +02 -> +03 and autumn +03 -> +02 at 01:00 UTC.
        var springBefore = new DateTimeOffset(2044, 3, 27, 0, 59, 59, TimeSpan.Zero);
        var autumnBefore = new DateTimeOffset(2044, 10, 30, 0, 59, 59, TimeSpan.Zero);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
        Assert.NotEqual(zone.GetUtcOffset(springBefore), zone.GetUtcOffset(springBefore.AddSeconds(1)));
        Assert.NotEqual(zone.GetUtcOffset(autumnBefore), zone.GetUtcOffset(autumnBefore.AddSeconds(1)));
        var first = await Seed(user.Id, springBefore, 0.10m); var second = await Seed(user.Id, springBefore.AddSeconds(1), 0.20m);
        await Seed(user.Id, autumnBefore, 7.25m); await Seed(user.Id, autumnBefore.AddSeconds(1), 9.75m);
        using var client = await Login(super);
        var spring = await Json(await client.GetAsync("/api/admin/reports/finance?year=2044&month=3&sort=amount&direction=asc&page=2&pageSize=1"));
        Assert.Equal(2, spring.GetProperty("completedCount").GetInt32()); Assert.Equal(0.30m, Number(spring, "revenue"));
        Assert.Equal(second, spring.GetProperty("orders")[0].GetProperty("id").GetGuid());
        var byDate = await Json(await client.GetAsync("/api/admin/reports/finance?year=2044&month=3&sort=date&direction=asc&pageSize=1"));
        Assert.Equal(first, byDate.GetProperty("orders")[0].GetProperty("id").GetGuid());
        var filtered = await Json(await client.GetAsync("/api/admin/reports/finance?year=2044&month=3&fromDate=2044-03-27&toDate=2044-03-27&minAmount=0.15&maxAmount=0.20"));
        Assert.Equal(2, filtered.GetProperty("completedCount").GetInt32()); Assert.Equal(0.30m, Number(filtered, "revenue"));
        Assert.Equal(1, filtered.GetProperty("filteredCount").GetInt32());
        Assert.Equal(second, filtered.GetProperty("orders")[0].GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/reports/finance?year=2044&month=3&minAmount=0.12345678901234567890123456789")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/reports/finance?year=2044&month=3&maxAmount=79228162514264337593543950336")).StatusCode);
        var autumn = await Json(await client.GetAsync("/api/admin/reports/finance?year=2044&month=10"));
        Assert.Equal(2, autumn.GetProperty("completedCount").GetInt32()); Assert.Equal(17m, Number(autumn, "revenue"));
    }

    [PostgresFact]
    public async Task ExpenseCreationAndTwoCorrectionsKeepOriginalMonthAndAuditHistory()
    {
        var super = await CreateUser("SuperAdmin"); using var client = await Login(super);
        using var created = await client.PostAsJsonAsync("/api/admin/reports/expenses", new { amount = 0.10m, description = "Test supplies" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); var original = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = original.GetProperty("id").GetGuid(); var version = original.GetProperty("version").GetUInt32();
        Assert.Equal(2072, original.GetProperty("recordedYear").GetInt32()); Assert.Equal(9, original.GetProperty("recordedMonth").GetInt32());
        var time = original.GetProperty("recordedAtUtc").GetDateTimeOffset();
        clock.Advance(TimeSpan.FromSeconds(2));
        var first = await Json(await client.PatchAsJsonAsync($"/api/admin/reports/expenses/{id}", new { version, amount = 12.34m, description = "Adjusted" }));
        version = first.GetProperty("version").GetUInt32(); clock.Advance(TimeSpan.FromDays(32));
        using var later = await Login(super);
        var second = await Json(await later.PatchAsJsonAsync($"/api/admin/reports/expenses/{id}", new { version, amount = 15.67m, description = (string?)null }));
        Assert.Equal(9, second.GetProperty("recordedMonth").GetInt32()); Assert.Equal(time, second.GetProperty("recordedAtUtc").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("description").ValueKind);
        var report = await Json(await later.GetAsync("/api/admin/reports/finance?year=2072&month=9"));
        Assert.Equal(15.67m, Number(report, "expenses")); Assert.Equal(-15.67m, Number(report, "result"));
        var other = await Json(await later.GetAsync("/api/admin/reports/expenses?year=2072&month=10&sort=amount&direction=asc"));
        Assert.Equal(0, other.GetProperty("total").GetInt32());
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var audit = await db.AdminAuditEvents.Where(x => x.ObjectId == id.ToString() && x.ObjectType == "MonthlyExpense").OrderBy(x => x.OccurredAtUtc).ToListAsync();
        Assert.Equal(new[] { "ExpenseCreated", "ExpenseCorrected", "ExpenseCorrected" }, audit.Select(x => x.Action));
        Assert.All(audit, x => Assert.Equal(super.Id, x.ActorId));
        using var details1 = JsonDocument.Parse(audit[1].Details); using var details2 = JsonDocument.Parse(audit[2].Details);
        Assert.Equal(0.10m, details1.RootElement.GetProperty("before").GetProperty("amount").GetDecimal());
        Assert.Equal(12.34m, details1.RootElement.GetProperty("after").GetProperty("amount").GetDecimal());
        Assert.Equal(12.34m, details2.RootElement.GetProperty("before").GetProperty("amount").GetDecimal());
        Assert.Equal(15.67m, details2.RootElement.GetProperty("after").GetProperty("amount").GetDecimal());
        Assert.True(details1.RootElement.GetProperty("before").GetProperty("hasDescription").GetBoolean());
        Assert.True(details1.RootElement.GetProperty("after").GetProperty("hasDescription").GetBoolean());
        Assert.True(details1.RootElement.GetProperty("descriptionChanged").GetBoolean());
        Assert.True(details2.RootElement.GetProperty("before").GetProperty("hasDescription").GetBoolean());
        Assert.False(details2.RootElement.GetProperty("after").GetProperty("hasDescription").GetBoolean());
        Assert.DoesNotContain("Test supplies", string.Join(' ', audit.Select(a => a.Details)));
        var history = await later.GetFromJsonAsync<JsonElement>($"/api/admin/audit-log?actorId={super.Id}&action=ExpenseCorrected&direction=asc");
        var corrections = history.GetProperty("items").EnumerateArray().Where(e => e.GetProperty("objectId").GetString() == id.ToString()).ToArray();
        Assert.Equal(2, corrections.Length);
        Assert.Equal(0.10m, corrections[0].GetProperty("details").GetProperty("before").GetProperty("amount").GetDecimal());
        Assert.Equal(12.34m, corrections[0].GetProperty("details").GetProperty("after").GetProperty("amount").GetDecimal());
        Assert.Equal(12.34m, corrections[1].GetProperty("details").GetProperty("before").GetProperty("amount").GetDecimal());
        Assert.Equal(15.67m, corrections[1].GetProperty("details").GetProperty("after").GetProperty("amount").GetDecimal());
        Assert.Equal("12.34", corrections[0].GetProperty("details").GetProperty("after").GetProperty("amountExact").GetString());
        Assert.DoesNotContain("Test supplies", history.ToString());
        Assert.DoesNotContain("Adjusted", history.ToString());
    }

    [PostgresFact]
    public async Task FinanceRejectsUnauthorizedCsrfInvalidAndConflictingCorrections()
    {
        clock.Advance(TimeSpan.FromDays(366));
        var super = await CreateUser("SuperAdmin"); var admin = await CreateUser("Admin"); var user = await CreateUser("User");
        using var s = await Login(super); using var a = await Login(admin); using var u = await Login(user); using var anonymous = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/reports/finance?year=2071&month=9")).StatusCode);
        foreach (var other in new[] { a, u })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/admin/reports/finance?year=2071&month=9")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/admin/reports/expenses?year=2071&month=9")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync("/api/admin/reports/expenses", new { amount = 2m })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.PatchAsJsonAsync($"/api/admin/reports/expenses/{Guid.NewGuid()}", new { version = 1, amount = 2m })).StatusCode);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await s.GetAsync("/api/admin/reports/finance?year=2071&month=13")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.GetAsync("/api/admin/reports/finance?year=2071&month=9&minAmount=10&maxAmount=2")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.GetAsync("/api/admin/reports/finance?year=2071&month=9&fromDate=2071-10-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.PostAsJsonAsync("/api/admin/reports/expenses", new { amount = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.PostAsJsonAsync("/api/admin/reports/expenses", new { amount = 1, recordedMonth = 1 })).StatusCode);
        s.DefaultRequestHeaders.Remove("RequestVerificationToken");
        Assert.Equal(HttpStatusCode.BadRequest, (await s.PostAsJsonAsync("/api/admin/reports/expenses", new { amount = 2 })).StatusCode);
        await Token(s); using var created = await s.PostAsJsonAsync("/api/admin/reports/expenses", new { amount = 2m });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); var item = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = item.GetProperty("id").GetGuid(); var version = item.GetProperty("version").GetUInt32();
        s.DefaultRequestHeaders.Remove("RequestVerificationToken");
        Assert.Equal(HttpStatusCode.BadRequest, (await s.PatchAsJsonAsync($"/api/admin/reports/expenses/{id}", new { version, amount = 4m })).StatusCode);
        await Token(s);
        var responses = await Task.WhenAll(s.PatchAsJsonAsync($"/api/admin/reports/expenses/{id}", new { version, amount = 5m }),
            s.PatchAsJsonAsync($"/api/admin/reports/expenses/{id}", new { version, amount = 7m }));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK); Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        var winner = await Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK).Content.ReadFromJsonAsync<JsonElement>();
        var nextVersion = winner.GetProperty("version").GetUInt32(); var sameAmount = winner.GetProperty("amount").GetDecimal();
        var noOp = await Task.WhenAll(s.PatchAsJsonAsync($"/api/admin/reports/expenses/{id}", new { version = nextVersion, amount = sameAmount }),
            s.PatchAsJsonAsync($"/api/admin/reports/expenses/{id}", new { version = nextVersion, amount = sameAmount }));
        Assert.Single(noOp, x => x.StatusCode == HttpStatusCode.OK); Assert.Single(noOp, x => x.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.NotFound, (await s.PatchAsJsonAsync($"/api/admin/reports/expenses/{Guid.NewGuid()}", new { version, amount = 5m })).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(2, await db.AdminAuditEvents.CountAsync(x => x.ObjectId == id.ToString() && x.Action == "ExpenseCorrected"));
    }

    [PostgresFact]
    public async Task ActualCompletionRequiresPaymentAndHandoverBeforeReportIncludesOrder()
    {
        clock.SetUtcNow(new DateTimeOffset(2074, 10, 15, 12, 0, 0, TimeSpan.Zero));
        var super = await CreateUser("SuperAdmin"); var admin = await CreateUser("Admin"); var owner = await CreateUser("User");
        var id = await Seed(owner.Id, clock.GetUtcNow(), 43.21m, AppointmentStatus.AwaitingPayment, confirmation: false, statusEvent: false);
        using var s = await Login(super); using var a = await Login(admin);
        var detail = await Json(await a.GetAsync($"/api/appointments/{id}"));
        var version = detail.GetProperty("version").GetUInt32();
        Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsJsonAsync($"/api/admin/appointments/{id}/transition",
            new { version, target = "Completed", paymentReceived = true, vehicleHandedOver = false })).StatusCode);
        var before = await Json(await s.GetAsync("/api/admin/reports/finance?year=2074&month=10"));
        Assert.Equal(0, before.GetProperty("completedCount").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await a.PostAsJsonAsync($"/api/admin/appointments/{id}/transition",
            new { version, target = "Completed", paymentReceived = true, vehicleHandedOver = true })).StatusCode);
        var after = await Json(await s.GetAsync("/api/admin/reports/finance?year=2074&month=10"));
        Assert.Equal(1, after.GetProperty("completedCount").GetInt32()); Assert.Equal(43.21m, Number(after, "revenue"));
        Assert.Equal(id, after.GetProperty("orders")[0].GetProperty("id").GetGuid());
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var payment = await db.PaymentHandoverConfirmations.SingleAsync(x => x.AppointmentId == id);
        var completed = await db.AppointmentStatusEvents.SingleAsync(x => x.AppointmentId == id && x.ToStatus == AppointmentStatus.Completed);
        Assert.Equal(payment.PaidAtUtc, payment.HandedOverAtUtc); Assert.Equal(payment.PaidAtUtc, completed.ChangedAtUtc);
    }

    [PostgresFact]
    public async Task ExpenseListSortsAndPaginatesWithinItsRecordedMonth()
    {
        clock.SetUtcNow(new DateTimeOffset(2076, 6, 15, 10, 0, 0, TimeSpan.Zero));
        var super = await CreateUser("SuperAdmin"); using var client = await Login(super);
        var ids = new List<Guid>();
        foreach (var amount in new[] { 50m, 5m, 25m })
        {
            using var response = await client.PostAsJsonAsync("/api/admin/reports/expenses", new { amount });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            ids.Add((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        var page = await Json(await client.GetAsync("/api/admin/reports/expenses?year=2076&month=6&sort=amount&direction=asc&page=1&pageSize=2"));
        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Equal(new[] { ids[1], ids[2] }, page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()));
        var byDate = await Json(await client.GetAsync("/api/admin/reports/expenses?year=2076&month=6&sort=date&direction=desc&pageSize=1"));
        Assert.Equal(ids[2], byDate.GetProperty("items")[0].GetProperty("id").GetGuid());
    }

    [PostgresFact]
    public async Task OverpreciseExpenseInputsAreRejectedInsteadOfRoundedOnCreateAndCorrection()
    {
        clock.SetUtcNow(new DateTimeOffset(2085, 6, 10, 10, 0, 0, TimeSpan.Zero));
        var super = await CreateUser("SuperAdmin"); using var client = await Login(super);
        static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");
        foreach (var amount in new[] { "0.12345678901234567890123456789", "7922816251426433759354395033.51", "1e-29" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/admin/reports/expenses", JsonBody($"{{\"amount\":{amount}}}"))).StatusCode);
        using var created = await client.PostAsync("/api/admin/reports/expenses", JsonBody("{\"amount\":0.1234567890123456789012345678}"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var item = await created.Content.ReadFromJsonAsync<JsonElement>(); var id = item.GetProperty("id").GetGuid(); var version = item.GetProperty("version").GetUInt32();
        foreach (var amount in new[] { "0.12345678901234567890123456789", "7922816251426433759354395033.51" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsync($"/api/admin/reports/expenses/{id}", JsonBody($"{{\"version\":{version},\"amount\":{amount}}}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/admin/reports/expenses", JsonBody("{\"amount\":1e-28}"))).StatusCode);
        var report = await Json(await client.GetAsync("/api/admin/reports/finance?year=2085&month=6"));
        Assert.Equal("0.1234567890123456789012345679", report.GetProperty("expensesExact").GetString());
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var saved = await db.MonthlyExpenses.SingleAsync(e => e.Id == id);
        Assert.Equal(0.1234567890123456789012345678m, saved.Amount);
        Assert.Equal(1, await db.AdminAuditEvents.CountAsync(e => e.ObjectId == id.ToString()));
    }

    [PostgresFact]
    public async Task ConcurrentMaximumExpensesReturnControlledErrorAndRemainCorrectableWithAudit()
    {
        clock.SetUtcNow(new DateTimeOffset(2080, 1, 15, 10, 0, 0, TimeSpan.Zero));
        var firstAdmin = await CreateUser("SuperAdmin"); var secondAdmin = await CreateUser("SuperAdmin");
        var owner = await CreateUser("User"); var completed = await Seed(owner.Id, clock.GetUtcNow(), 10.25m);
        using var first = await Login(firstAdmin); using var second = await Login(secondAdmin);
        var created = await Task.WhenAll(first.PostAsJsonAsync("/api/admin/reports/expenses", new { amount = decimal.MaxValue }),
            second.PostAsJsonAsync("/api/admin/reports/expenses", new { amount = decimal.MaxValue }));
        Assert.All(created, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        var items = await Task.WhenAll(created.Select(response => response.Content.ReadFromJsonAsync<JsonElement>()));
        var reportUrl = "/api/admin/reports/finance?year=2080&month=1";
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await first.GetAsync(reportUrl)).StatusCode);
        var list = await Json(await first.GetAsync("/api/admin/reports/expenses?year=2080&month=1&pageSize=100"));
        Assert.Equal(2, list.GetProperty("total").GetInt32());
        clock.Advance(TimeSpan.FromDays(40)); using var later = await Login(firstAdmin);
        var firstId = items[0].GetProperty("id").GetGuid(); var version = items[0].GetProperty("version").GetUInt32();
        var competing = await Task.WhenAll(later.PatchAsJsonAsync($"/api/admin/reports/expenses/{firstId}", new { version, amount = 12.34m }),
            later.PatchAsJsonAsync($"/api/admin/reports/expenses/{firstId}", new { version, amount = 12.34m }));
        Assert.Single(competing, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(competing, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await later.GetAsync(reportUrl)).StatusCode);
        var secondId = items[1].GetProperty("id").GetGuid();
        var corrected = await Json(await later.PatchAsJsonAsync($"/api/admin/reports/expenses/{secondId}", new { version = items[1].GetProperty("version").GetUInt32(), amount = 23.45m }));
        Assert.Equal(2080, corrected.GetProperty("recordedYear").GetInt32()); Assert.Equal(1, corrected.GetProperty("recordedMonth").GetInt32());
        Assert.Equal(items[1].GetProperty("recordedAtUtc").GetDateTimeOffset(), corrected.GetProperty("recordedAtUtc").GetDateTimeOffset());
        var report = await Json(await later.GetAsync(reportUrl));
        Assert.Equal(35.79m, Number(report, "expenses")); Assert.Equal(10.25m, Number(report, "revenue")); Assert.Equal(-25.54m, Number(report, "result"));
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var order = await db.Appointments.AsNoTracking().Include(a => a.RepairPlan).SingleAsync(a => a.Id == completed);
        Assert.Equal(AppointmentStatus.Completed, order.Status); Assert.Equal(10.25m, order.RepairPlan!.AgreedPrice);
        var audits = await db.AdminAuditEvents.Where(e => e.ObjectType == "MonthlyExpense" && (e.ObjectId == firstId.ToString() || e.ObjectId == secondId.ToString())).ToListAsync();
        Assert.Equal(4, audits.Count); Assert.Equal(2, audits.Count(e => e.Action == "ExpenseCorrected"));
        Assert.All(audits.Where(e => e.Action == "ExpenseCorrected"), e => {
            using var details = JsonDocument.Parse(e.Details);
            Assert.Equal(decimal.MaxValue, details.RootElement.GetProperty("before").GetProperty("amount").GetDecimal());
        });
    }

    [PostgresFact]
    public async Task RevenueExpenseAndDifferenceNeverRoundOrOverflowSilently()
    {
        var super = await CreateUser("SuperAdmin"); var owner = await CreateUser("User"); using var client = await Login(super);
        var maxAt = new DateTimeOffset(2082, 3, 10, 10, 0, 0, TimeSpan.Zero);
        await Seed(owner.Id, maxAt, decimal.MaxValue);
        var exact = await Json(await client.GetAsync("/api/admin/reports/finance?year=2082&month=3"));
        Assert.Equal(decimal.MaxValue, Number(exact, "revenue"));
        await Seed(owner.Id, maxAt.AddDays(1), 0.1m);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync("/api/admin/reports/finance?year=2082&month=3")).StatusCode);
        clock.SetUtcNow(new DateTimeOffset(2083, 4, 10, 10, 0, 0, TimeSpan.Zero));
        await Seed(owner.Id, clock.GetUtcNow(), decimal.MaxValue);
        using var later = await Login(super);
        Assert.Equal(HttpStatusCode.Created, (await later.PostAsJsonAsync("/api/admin/reports/expenses", new { amount = 0.1m })).StatusCode);
        // MaxValue - 0.1 would round silently under ordinary decimal subtraction.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await later.GetAsync("/api/admin/reports/finance?year=2083&month=4")).StatusCode);
        clock.SetUtcNow(new DateTimeOffset(2084, 5, 10, 10, 0, 0, TimeSpan.Zero)); using var last = await Login(super);
        Assert.Equal(HttpStatusCode.Created, (await last.PostAsJsonAsync("/api/admin/reports/expenses", new { amount = decimal.MaxValue })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await last.PostAsJsonAsync("/api/admin/reports/expenses", new { amount = 0.1m })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await last.GetAsync("/api/admin/reports/finance?year=2084&month=5")).StatusCode);
    }

    [PostgresFact]
    public async Task AuditFailureRollsBackExpenseCreationAndCorrection()
    {
        clock.SetUtcNow(new DateTimeOffset(2077, 5, 1, 12, 0, 0, TimeSpan.Zero));
        var super = await CreateUser("SuperAdmin"); using var normal = await Login(super);
        using var created = await normal.PostAsJsonAsync("/api/admin/reports/expenses", new { amount = 8m });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); var item = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = item.GetProperty("id").GetGuid(); var version = item.GetProperty("version").GetUInt32();
        using var failing = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Scoped<IAdminAuditWriter, FailingAudit>())));
        using var client = failing.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
        await Token(client);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/login", new { phone = super.PhoneNormalized, password = Password })).StatusCode);
        await Token(client);
        Assert.Equal(HttpStatusCode.InternalServerError, (await client.PostAsJsonAsync("/api/admin/reports/expenses", new { amount = 999m })).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await client.PatchAsJsonAsync($"/api/admin/reports/expenses/{id}", new { version, amount = 999m })).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(8m, (await db.MonthlyExpenses.SingleAsync(x => x.Id == id)).Amount);
        Assert.False(await db.MonthlyExpenses.AnyAsync(x => x.Amount == 999m));
        Assert.Equal(1, await db.AdminAuditEvents.CountAsync(x => x.ObjectId == id.ToString() && x.ObjectType == "MonthlyExpense"));
    }

    private sealed class FailingAudit(ApplicationDbContext db, TimeProvider clock) : IAdminAuditWriter
    {
        public void RecordExpenseChange(string actorId, Guid expenseId, decimal? beforeAmount, decimal afterAmount, bool beforeHasDescription, bool afterHasDescription, bool descriptionChanged) =>
            new AdminAuditWriter(db, clock).RecordExpenseChange("missing-test-actor", expenseId, beforeAmount, afterAmount, beforeHasDescription, afterHasDescription, descriptionChanged);
        public void RecordWeeklyScheduleChange(string actorId, IReadOnlyList<WeeklyInterval> before, IReadOnlyList<WeeklyInterval> after) => throw new NotSupportedException();
        public void RecordDateExceptionChange(string actorId, DateOnly date, InspectionException? before, InspectionException? after) => throw new NotSupportedException();
        public void RecordAppointmentStatusChange(string actorId, Guid appointmentId, AppointmentStatus from, AppointmentStatus to, Guid? statusEventId = null) => throw new NotSupportedException();
        public void RecordAssignmentChange(string actorId, Guid assignmentId, Guid appointmentId, Guid employeeId, bool released, bool automatic) => throw new NotSupportedException();
        public Task<AuditPage> ReadAsync(AuditQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void RecordRepairPlanChange(string actorId, Guid appointmentId, bool priceChanged, bool datesChanged) => throw new NotSupportedException();
        public void RecordPriceItemChange(string actorId, Guid priceItemId, bool created) => throw new NotSupportedException();
        public void RecordManualContact(string actorId, Guid appointmentId, ManualContactReason reason) => throw new NotSupportedException();
        public void RecordAccountAction(string actorId, string targetId, string action, bool? value) => throw new NotSupportedException();
    }
}
