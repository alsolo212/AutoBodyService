namespace AutoBodyService.Domain.Prices;

/// <summary>An indicative service price, independent of agreed repair plans.</summary>
public sealed class PriceListItem
{
    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 1000;
    public const string Currency = "UAH";
    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;
    public string Description { get; private set; } = null!;
    public decimal Price { get; private set; }
    public bool IsFrom { get; private set; }
    public int SortOrder { get; private set; }
    public bool IsPublished { get; private set; }
    public bool IsArchived { get; private set; }

    private PriceListItem() { }
    public PriceListItem(Guid id, string name, string? description, decimal price, bool isFrom, int sortOrder, bool isPublished)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
        Id = id;
        Update(name, description, price, isFrom, sortOrder, isPublished, false);
    }

    public void Update(string name, string? description, decimal price, bool isFrom, int sortOrder, bool isPublished, bool isArchived)
    {
        if (!ValidName(name) || !ValidDescription(description) || price < 0 || (isArchived && isPublished))
            throw new ArgumentException("Invalid price item.");
        Name = name.Trim(); Description = (description ?? "").Trim(); Price = price;
        IsFrom = isFrom; SortOrder = sortOrder; IsPublished = isPublished; IsArchived = isArchived;
    }

    public static bool ValidName(string? value) => value is not null && value.Length <= MaxNameLength &&
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
    public static bool ValidDescription(string? value) => value is null || value.Length <= MaxDescriptionLength && !value.Any(char.IsControl);
}
