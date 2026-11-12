namespace AutoBodyService.Domain.Employees;

/// <summary>A service worker, independent of authentication accounts.</summary>
public sealed class Employee
{
    public const int MaxTextLength = 200;
    public Guid Id { get; private set; }
    public string FullName { get; private set; } = null!;
    public string Specialization { get; private set; } = null!;
    public bool IsActive { get; private set; }

    private Employee() { }

    public Employee(Guid id, string fullName, string specialization, bool isActive)
    {
        if (id == Guid.Empty) throw new ArgumentException("Employee ID is required.", nameof(id));
        Id = id;
        Update(fullName, specialization, isActive);
    }

    public void Update(string fullName, string specialization, bool isActive)
    {
        if (!IsValidText(fullName) || !IsValidText(specialization))
            throw new ArgumentException("Employee name and specialization are required and must be valid text.");
        FullName = fullName.Trim();
        Specialization = specialization.Trim();
        IsActive = isActive;
    }

    public static bool IsValidText(string? value) => value is not null && value.Length <= MaxTextLength &&
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
}
