using FCG.Catalog.Domain.Common;

namespace FCG.Catalog.Domain.Entities;

public sealed class Game
{
    public const int MaxTitleLength = 300;
    public const int MaxDescriptionLength = 2000;
    public const int MaxDeveloperLength = 200;

    private Game()
    {
    }

    public Guid Id { get; private set; }
    public string Title { get; private set; } = null!;
    public string? Description { get; private set; }
    public string? Developer { get; private set; }
    public decimal Price { get; private set; }
    public bool IsActive { get; private set; }
    public long SearchRevision { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? UpdatedAtUtc { get; private set; }

    public static Game Create(
        decimal price,
        string title,
        string? description,
        string? developer,
        DateTime createdAtUtc)
    {
        Validate(title, description, developer, price);

        return new Game
        {
            Id = Guid.NewGuid(),
            Title = title.Trim(),
            Description = NormalizeOptional(description),
            Developer = NormalizeOptional(developer),
            Price = price,
            IsActive = true,
            SearchRevision = 1,
            CreatedAtUtc = EnsureUtc(createdAtUtc)
        };
    }

    public void Update(
        decimal price,
        string title,
        string? description,
        string? developer,
        DateTime updatedAtUtc)
    {
        if (!IsActive)
        {
            throw new DomainValidationException("Inactive games cannot be updated.");
        }

        Validate(title, description, developer, price);

        SearchRevision = checked(SearchRevision + 1);
        Title = title.Trim();
        Description = NormalizeOptional(description);
        Developer = NormalizeOptional(developer);
        Price = price;
        UpdatedAtUtc = EnsureUtc(updatedAtUtc);
    }

    public void Deactivate(DateTime updatedAtUtc)
    {
        if (!IsActive)
        {
            return;
        }

        SearchRevision = checked(SearchRevision + 1);
        IsActive = false;
        UpdatedAtUtc = EnsureUtc(updatedAtUtc);
    }

    private static void Validate(
        string title,
        string? description,
        string? developer,
        decimal price)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new DomainValidationException("Title is required.");
        }

        if (title.Trim().Length > MaxTitleLength)
        {
            throw new DomainValidationException(
                $"Title must be at most {MaxTitleLength} characters.");
        }

        if (!string.IsNullOrWhiteSpace(description)
            && description.Trim().Length > MaxDescriptionLength)
        {
            throw new DomainValidationException(
                $"Description must be at most {MaxDescriptionLength} characters.");
        }

        if (!string.IsNullOrWhiteSpace(developer)
            && developer.Trim().Length > MaxDeveloperLength)
        {
            throw new DomainValidationException(
                $"Developer must be at most {MaxDeveloperLength} characters.");
        }

        if (price <= 0)
        {
            throw new DomainValidationException("Price must be greater than zero.");
        }
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime EnsureUtc(DateTime value)
    {
        if (value == default)
        {
            throw new DomainValidationException("Date is required.");
        }

        return DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}
