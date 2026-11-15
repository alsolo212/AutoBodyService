namespace AutoBodyService.Domain.Finance;

/// <summary>A general expense assigned to its original Kyiv calendar month.</summary>
public sealed class MonthlyExpense
{
    private MonthlyExpense() { CreatedById = null!; }
    public MonthlyExpense(Guid id, decimal amount, string? description, string actorId, DateTimeOffset recordedAtUtc, int year, int month)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (recordedAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Time must use UTC.", nameof(recordedAtUtc));
        if (year is < 1 or > 9999 || month is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(month));
        Id = id; CreatedById = actorId; RecordedAtUtc = recordedAtUtc; RecordedYear = year; RecordedMonth = month;
        SetValues(amount, description);
    }
    public Guid Id { get; private set; }
    public decimal Amount { get; private set; }
    public string? Description { get; private set; }
    public string CreatedById { get; private set; }
    public DateTimeOffset RecordedAtUtc { get; private set; }
    public int RecordedYear { get; private set; }
    public int RecordedMonth { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public static bool ValidDescription(string? description) => description is null || (description.Length <= 500 && !description.Any(char.IsControl));
    public void Correct(decimal amount, string? description, DateTimeOffset nowUtc)
    {
        if (nowUtc.Offset != TimeSpan.Zero || nowUtc < RecordedAtUtc || (UpdatedAtUtc.HasValue && nowUtc < UpdatedAtUtc))
            throw new ArgumentException("Invalid correction time.", nameof(nowUtc));
        SetValues(amount, description); UpdatedAtUtc = nowUtc;
    }
    private void SetValues(decimal amount, string? description)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (!ValidDescription(description)) throw new ArgumentException("Invalid description.", nameof(description));
        Amount = amount; Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
    }
}
