using FCG.Catalog.Api.Contracts.Games;
using FCG.Catalog.Infrastructure.Persistence.Search;

namespace FCG.Catalog.Api.Search;

public interface IGameSearchIndex
{
    Task WriteAsync(SearchIndexPending pending, CancellationToken cancellationToken = default);
    Task<GameResponse[]> SearchAsync(string query, CancellationToken cancellationToken = default);
    Task EnsureMappingAsync(CancellationToken cancellationToken = default);
}

public sealed class SearchUnavailableException() : Exception("Search is temporarily unavailable.");

