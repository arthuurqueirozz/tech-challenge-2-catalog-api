using FCG.Catalog.Domain.Entities;

namespace FCG.Catalog.Infrastructure.Persistence.Search;

public sealed record GameSearchDocument(
    Guid Id,
    string Title,
    string? Description,
    string? Developer,
    decimal Price,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc)
{
    public static GameSearchDocument FromGame(Game game) => new(
        game.Id,
        game.Title,
        game.Description,
        game.Developer,
        // Match SQL decimal(18,2), including midpoint rounding and serialization scale.
        decimal.Round(game.Price, 2, MidpointRounding.AwayFromZero) + 0.00m,
        game.IsActive,
        DateTime.SpecifyKind(game.CreatedAtUtc, DateTimeKind.Utc),
        game.UpdatedAtUtc is { } updated ? DateTime.SpecifyKind(updated, DateTimeKind.Utc) : null);
}
