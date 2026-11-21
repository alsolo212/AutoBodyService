using System.Data;
using System.Globalization;
using System.Numerics;
using AutoBodyService.Application.Audit;
using AutoBodyService.Application.Finance;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Domain.Finance;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoBodyService.Infrastructure.Finance;

public sealed class FinanceReports(ApplicationDbContext db, IAdminAuditWriter audit, TimeProvider clock) : IFinanceReports
{
    private static readonly TimeZoneInfo Kyiv = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");

    public async Task<FinanceReport> ReportAsync(int year, int month, OrderFilter filter, string sort, bool descending, int page, int pageSize, CancellationToken ct)
    {
        var (start, end) = MonthBounds(year, month);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        // One scalar completion instant per order prevents duplicate status events
        // from multiplying either the count or the agreed price.
        var eligible = from appointment in db.Appointments.AsNoTracking()
                       join plan in db.Set<RepairPlan>().AsNoTracking() on appointment.Id equals plan.AppointmentId
                       let paid = db.PaymentHandoverConfirmations.Where(p => p.AppointmentId == appointment.Id).Max(p => (DateTimeOffset?)p.PaidAtUtc)
                       let handed = db.PaymentHandoverConfirmations.Where(p => p.AppointmentId == appointment.Id && p.PaidAtUtc == paid).Max(p => (DateTimeOffset?)p.HandedOverAtUtc)
                       let completed = db.AppointmentStatusEvents.Where(e => e.AppointmentId == appointment.Id &&
                           e.FromStatus == AppointmentStatus.AwaitingPayment && e.ToStatus == AppointmentStatus.Completed &&
                           e.ChangedAtUtc >= paid && e.ChangedAtUtc >= handed)
                           .Select(e => (DateTimeOffset?)e.ChangedAtUtc).Min()
                       where appointment.Status == AppointmentStatus.Completed && completed >= start && completed < end
                       select new { appointment.Id, appointment.Make, appointment.Model, plan.AgreedPrice, Completed = completed!.Value };
        var count = await eligible.CountAsync(ct);
        // PostgreSQL numeric without declared precision has a much wider range than CLR decimal. Read its aggregate as text so
        // conversion to CLR decimal cannot silently round or overflow in the driver.
        var revenueText = await eligible.GroupBy(_ => 1).Select(g => g.Sum(x => x.AgreedPrice).ToString()).SingleOrDefaultAsync(ct);
        var costsText = await db.MonthlyExpenses.Where(x => x.RecordedYear == year && x.RecordedMonth == month)
            .GroupBy(_ => 1).Select(g => g.Sum(x => x.Amount).ToString()).SingleOrDefaultAsync(ct);
        var revenue = ParseExactTotal(revenueText);
        var costs = ParseExactTotal(costsText);
        var result = SubtractExactly(revenue, costs);
        var filtered = eligible;
        if (filter.FromDate is { } from) { var bound = KyivMidnight(from); filtered = filtered.Where(x => x.Completed >= bound); }
        if (filter.ToDate is { } to) { var bound = KyivMidnight(to.AddDays(1)); filtered = filtered.Where(x => x.Completed < bound); }
        if (filter.MinimumAmount is { } minimum) filtered = filtered.Where(x => x.AgreedPrice >= minimum);
        if (filter.MaximumAmount is { } maximum) filtered = filtered.Where(x => x.AgreedPrice <= maximum);
        var filteredCount = await filtered.CountAsync(ct);
        var ordered = sort == "amount"
            ? (descending ? filtered.OrderByDescending(x => x.AgreedPrice) : filtered.OrderBy(x => x.AgreedPrice))
            : (descending ? filtered.OrderByDescending(x => x.Completed) : filtered.OrderBy(x => x.Completed));
        var rows = await ordered.ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new CompletedOrder(x.Id, x.Make, x.Model, x.Completed, x.AgreedPrice)).ToListAsync(ct);
        await transaction.CommitAsync(ct);
        return new(year, month, count, revenue, costs, result, filteredCount, page, pageSize, rows);
    }

    public async Task<ExpensePage> ExpensesAsync(int year, int month, string sort, bool descending, int page, int pageSize, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var query = db.MonthlyExpenses.AsNoTracking().Where(x => x.RecordedYear == year && x.RecordedMonth == month);
        var total = await query.CountAsync(ct);
        var ordered = sort == "amount"
            ? (descending ? query.OrderByDescending(x => x.Amount) : query.OrderBy(x => x.Amount))
            : (descending ? query.OrderByDescending(x => x.RecordedAtUtc) : query.OrderBy(x => x.RecordedAtUtc));
        var rows = await ordered.ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new ExpenseItem(x.Id, x.Amount, x.Description, x.RecordedAtUtc, x.RecordedYear, x.RecordedMonth,
                x.UpdatedAtUtc, EF.Property<uint>(x, "Version"))).ToListAsync(ct);
        await transaction.CommitAsync(ct);
        return new(year, month, total, page, pageSize, rows);
    }

    public async Task<ExpenseItem> CreateAsync(decimal amount, string? description, string actorId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var local = TimeZoneInfo.ConvertTime(now, Kyiv);
        var expense = new MonthlyExpense(Guid.NewGuid(), amount, description, actorId, now, local.Year, local.Month);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.MonthlyExpenses.Add(expense);
        audit.RecordExpenseChange(actorId, expense.Id, null, amount, false, expense.Description is not null, expense.Description is not null);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Details(expense);
    }

    public async Task<(ExpenseUpdateResult Result, ExpenseItem? Item)> UpdateAsync(Guid id, uint version, ExpenseChange change, string actorId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var expense = await db.MonthlyExpenses.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (expense is null) return (ExpenseUpdateResult.NotFound, null);
        if (db.Entry(expense).Property<uint>("Version").CurrentValue != version) return (ExpenseUpdateResult.Conflict, null);
        var beforeAmount = expense.Amount;
        var beforeDescription = expense.Description;
        var newDescription = change.DescriptionSupplied ? change.Description : beforeDescription;
        expense.Correct(change.Amount ?? beforeAmount, newDescription, clock.GetUtcNow());
        // Even an identical correction must participate in the xmin check.
        db.Entry(expense).Property(x => x.Amount).IsModified = true;
        audit.RecordExpenseChange(actorId, id, beforeAmount, expense.Amount, beforeDescription is not null,
            expense.Description is not null, beforeDescription != expense.Description);
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return (ExpenseUpdateResult.Success, Details(expense));
        }
        catch (DbUpdateConcurrencyException) { return (ExpenseUpdateResult.Conflict, null); }
    }

    // Preserve every decimal digit. CLR decimal arithmetic can round a difference
    // even when both operands are individually representable.
    private static decimal ParseExactTotal(string? text)
    {
        if (text is null) return 0m;
        var negative = text.StartsWith('-');
        var unsigned = negative ? text[1..] : text;
        var dot = unsigned.IndexOf('.');
        var scale = dot < 0 ? 0 : unsigned.Length - dot - 1;
        var digits = dot < 0 ? unsigned : unsigned.Remove(dot, 1);
        var coefficient = BigInteger.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        return FromExactParts(negative ? -coefficient : coefficient, scale);
    }

    private static decimal SubtractExactly(decimal left, decimal right)
    {
        static (BigInteger Value, int Scale) Parts(decimal value)
        {
            var bits = decimal.GetBits(value);
            var coefficient = (BigInteger)(uint)bits[0] | ((BigInteger)(uint)bits[1] << 32) | ((BigInteger)(uint)bits[2] << 64);
            return ((bits[3] & int.MinValue) == 0 ? coefficient : -coefficient, (bits[3] >> 16) & 0xff);
        }
        var (a, aScale) = Parts(left); var (b, bScale) = Parts(right);
        var scale = Math.Max(aScale, bScale);
        return FromExactParts(a * BigInteger.Pow(10, scale - aScale) - b * BigInteger.Pow(10, scale - bScale), scale);
    }

    private static decimal FromExactParts(BigInteger signed, int scale)
    {
        var negative = signed.Sign < 0;
        var coefficient = BigInteger.Abs(signed);
        while (scale > 0 && coefficient % 10 == 0) { coefficient /= 10; scale--; }
        if (scale > 28 || coefficient > (BigInteger)decimal.MaxValue) throw new OverflowException("Financial total is not exactly representable.");
        var lo = unchecked((int)(uint)(coefficient & uint.MaxValue));
        var mid = unchecked((int)(uint)((coefficient >> 32) & uint.MaxValue));
        var hi = unchecked((int)(uint)((coefficient >> 64) & uint.MaxValue));
        return new decimal(lo, mid, hi, negative, (byte)scale);
    }

    private ExpenseItem Details(MonthlyExpense x) => new(x.Id, x.Amount, x.Description, x.RecordedAtUtc, x.RecordedYear,
        x.RecordedMonth, x.UpdatedAtUtc, db.Entry(x).Property<uint>("Version").CurrentValue);

    internal static (DateTimeOffset Start, DateTimeOffset End) MonthBounds(int year, int month)
    {
        var first = new DateOnly(year, month, 1);
        return (KyivMidnight(first), KyivMidnight(first.AddMonths(1)));
    }
    private static DateTimeOffset KyivMidnight(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (Kyiv.IsInvalidTime(local)) local = local.AddMinutes(1);
        if (Kyiv.IsAmbiguousTime(local))
            return new DateTimeOffset(local, Kyiv.GetAmbiguousTimeOffsets(local).Max()).ToUniversalTime();
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, Kyiv));
    }
}
