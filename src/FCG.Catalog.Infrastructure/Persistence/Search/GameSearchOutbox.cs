using FCG.Catalog.Domain.Abstractions;
using FCG.Catalog.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FCG.Catalog.Infrastructure.Persistence.Search;

public sealed class GameSearchOutbox(CatalogDbContext context) : IGameSearchOutbox
{
    public async Task AddAsync(Game game, DateTime createdAtUtc, CancellationToken cancellationToken = default)
    {
        await context.SearchIndexPending.AddAsync(SearchIndexPending.Create(game, createdAtUtc), cancellationToken);
    }

    public async Task<IReadOnlyList<SearchIndexPending>> GetEligibleAsync(
        DateTime nowUtc, int batchSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batchSize, 500);
        return await context.SearchIndexPending.AsNoTracking()
            .Where(item => item.CompletedAtUtc == null && item.NextAttemptAtUtc <= nowUtc)
            .OrderBy(item => item.NextAttemptAtUtc).ThenBy(item => item.CreatedAtUtc).ThenBy(item => item.Id)
            .Take(batchSize).ToListAsync(cancellationToken);
    }

    public Task<int> CompleteAsync(Guid id, DateTime completedAtUtc, CancellationToken cancellationToken = default) =>
        context.SearchIndexPending.Where(item => item.Id == id && item.CompletedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.CompletedAtUtc, DateTime.SpecifyKind(completedAtUtc, DateTimeKind.Utc))
                .SetProperty(item => item.LastErrorCode, (string?)null), cancellationToken);

    public Task<int> FailAsync(Guid id, string errorCode, DateTime nextAttemptAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        if (errorCode.Length > 100 || errorCode.Any(character => !char.IsAsciiLetterOrDigit(character)
                && character != '_' && character != '-'))
        {
            throw new ArgumentException("Use a short diagnostic code, not exception text or payload.", nameof(errorCode));
        }

        return context.SearchIndexPending.Where(item => item.Id == id && item.CompletedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Attempts, item => item.Attempts + 1)
                .SetProperty(item => item.NextAttemptAtUtc, DateTime.SpecifyKind(nextAttemptAtUtc, DateTimeKind.Utc))
                .SetProperty(item => item.LastErrorCode, errorCode), cancellationToken);
    }
}
