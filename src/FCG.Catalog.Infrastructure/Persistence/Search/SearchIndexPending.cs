using System.Text.Json;
using FCG.Catalog.Domain.Entities;

namespace FCG.Catalog.Infrastructure.Persistence.Search;

public sealed class SearchIndexPending
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private SearchIndexPending() { }

    public Guid Id { get; private set; }
    public Guid GameId { get; private set; }
    public long Revision { get; private set; }
    public string PayloadJson { get; private set; } = null!;
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    public int Attempts { get; private set; }
    public DateTime NextAttemptAtUtc { get; private set; }
    public string? LastErrorCode { get; private set; }

    public static SearchIndexPending Create(Game game, DateTime createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.SearchRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(game), "Search revision must be positive.");
        }

        var timestamp = DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc);
        return new SearchIndexPending
        {
            Id = Guid.NewGuid(),
            GameId = game.Id,
            Revision = game.SearchRevision,
            PayloadJson = JsonSerializer.Serialize(GameSearchDocument.FromGame(game), JsonOptions),
            CreatedAtUtc = timestamp,
            NextAttemptAtUtc = timestamp
        };
    }
}
