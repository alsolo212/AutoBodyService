using System.Globalization;

namespace AutoBodyService.Application.Finance;

public sealed record CompletedOrder(Guid Id, string Make, string Model, DateTimeOffset CompletedAtUtc, decimal Amount)
{
    public string AmountExact => Amount.ToString(CultureInfo.InvariantCulture);
}
public sealed record ExpenseItem(Guid Id, decimal Amount, string? Description, DateTimeOffset RecordedAtUtc,
    int RecordedYear, int RecordedMonth, DateTimeOffset? UpdatedAtUtc, uint Version)
{
    public string AmountExact => Amount.ToString(CultureInfo.InvariantCulture);
}
public sealed record FinanceReport(int Year, int Month, int CompletedCount, decimal Revenue, decimal Expenses, decimal Result,
    int FilteredCount, int Page, int PageSize, IReadOnlyList<CompletedOrder> Orders)
{
    public string Currency => "UAH";
    public string RevenueExact => Revenue.ToString(CultureInfo.InvariantCulture);
    public string ExpensesExact => Expenses.ToString(CultureInfo.InvariantCulture);
    public string ResultExact => Result.ToString(CultureInfo.InvariantCulture);
}
public sealed record ExpensePage(int Year, int Month, int Total, int Page, int PageSize, IReadOnlyList<ExpenseItem> Items);
public sealed record ExpenseChange(decimal? Amount, bool DescriptionSupplied, string? Description);
public sealed record OrderFilter(DateOnly? FromDate, DateOnly? ToDate, decimal? MinimumAmount, decimal? MaximumAmount);
public enum ExpenseUpdateResult { Success, NotFound, Conflict }
public interface IFinanceReports
{
    Task<FinanceReport> ReportAsync(int year, int month, OrderFilter filter, string sort, bool descending, int page, int pageSize, CancellationToken cancellationToken);
    Task<ExpensePage> ExpensesAsync(int year, int month, string sort, bool descending, int page, int pageSize, CancellationToken cancellationToken);
    Task<ExpenseItem> CreateAsync(decimal amount, string? description, string actorId, CancellationToken cancellationToken);
    Task<(ExpenseUpdateResult Result, ExpenseItem? Item)> UpdateAsync(Guid id, uint version, ExpenseChange change, string actorId, CancellationToken cancellationToken);
}
