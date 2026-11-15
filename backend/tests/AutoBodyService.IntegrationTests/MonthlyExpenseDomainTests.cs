using AutoBodyService.Domain.Finance;

namespace AutoBodyService.IntegrationTests;

public sealed class MonthlyExpenseDomainTests
{
    [Fact]
    public void ExpenseRejectsInvalidAmountsDescriptionAndNonUtcTime()
    {
        var now = new DateTimeOffset(2072, 9, 30, 20, 59, 59, TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MonthlyExpense(Guid.NewGuid(), 0m, null, "actor", now, 2072, 9));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MonthlyExpense(Guid.NewGuid(), -1m, null, "actor", now, 2072, 9));
        Assert.Throws<ArgumentException>(() => new MonthlyExpense(Guid.NewGuid(), 1m, new string('x', 501), "actor", now, 2072, 9));
        Assert.Throws<ArgumentException>(() => new MonthlyExpense(Guid.NewGuid(), 1m, "bad\ntext", "actor", now, 2072, 9));
        Assert.Throws<ArgumentException>(() => new MonthlyExpense(Guid.NewGuid(), 1m, null, "actor", now.ToOffset(TimeSpan.FromHours(3)), 2072, 9));
    }

    [Fact]
    public void CorrectionChangesOnlyAllowedFieldsAndPreservesOriginalPeriod()
    {
        var now = new DateTimeOffset(2072, 9, 30, 20, 59, 59, TimeSpan.Zero);
        var item = new MonthlyExpense(Guid.NewGuid(), 0.10m, "  Initial  ", "actor", now, 2072, 9);
        var updated = now.AddDays(35); item.Correct(0.20m, null, updated);
        Assert.Equal(0.20m, item.Amount); Assert.Null(item.Description);
        Assert.Equal(now, item.RecordedAtUtc); Assert.Equal(2072, item.RecordedYear); Assert.Equal(9, item.RecordedMonth);
        Assert.Equal(updated, item.UpdatedAtUtc); Assert.Equal("actor", item.CreatedById);
        Assert.Throws<ArgumentException>(() => item.Correct(2m, null, now));
        Assert.Equal(0.20m, item.Amount);
    }
}
