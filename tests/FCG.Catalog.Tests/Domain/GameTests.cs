using FCG.Catalog.Domain.Common;
using FCG.Catalog.Domain.Entities;

namespace FCG.Catalog.Tests.Domain;

public sealed class GameTests
{
    [Fact]
    public void Create_normalizes_optional_text()
    {
        var createdAt = new DateTime(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);

        var game = Game.Create(
            59.90m,
            "  Game  ",
            "  Description  ",
            "  Studio  ",
            createdAt);

        Assert.Equal("Game", game.Title);
        Assert.Equal("Description", game.Description);
        Assert.Equal("Studio", game.Developer);
        Assert.Equal(59.90m, game.Price);
        Assert.True(game.IsActive);
        Assert.Equal(1, game.SearchRevision);
        Assert.Equal(createdAt, game.CreatedAtUtc);
    }

    [Fact]
    public void Create_rejects_zero_price()
    {
        var exception = Assert.Throws<DomainValidationException>(() =>
            Game.Create(
                0,
                "Free Game",
                null,
                null,
                DateTime.UtcNow));

        Assert.Equal("Price must be greater than zero.", exception.Message);
    }

    [Fact]
    public void Deactivate_is_logical_delete()
    {
        var game = Game.Create(
            10,
            "Game",
            null,
            null,
            DateTime.UtcNow);
        var deletedAt = new DateTime(2026, 7, 8, 13, 0, 0, DateTimeKind.Utc);

        game.Deactivate(deletedAt);

        Assert.False(game.IsActive);
        Assert.Equal(deletedAt, game.UpdatedAtUtc);
        Assert.Equal(2, game.SearchRevision);
        game.Deactivate(deletedAt.AddMinutes(1));
        Assert.Equal(2, game.SearchRevision);
        Assert.Equal(deletedAt, game.UpdatedAtUtc);
    }

    [Fact]
    public void Update_increments_revision_but_invalid_update_does_not()
    {
        var game = Game.Create(10m, "Game", null, null, DateTime.UtcNow);
        game.Update(20m, "Edited", null, null, DateTime.UtcNow);
        Assert.Equal(2, game.SearchRevision);
        Assert.Throws<DomainValidationException>(() => game.Update(0m, "Invalid", null, null, DateTime.UtcNow));
        Assert.Equal(2, game.SearchRevision);
        Assert.Equal("Edited", game.Title);
    }
}
