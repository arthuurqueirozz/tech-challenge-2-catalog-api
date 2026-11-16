using FCG.Catalog.Domain.Entities;

namespace FCG.Catalog.Domain.Abstractions;

public interface IGameSearchOutbox
{
    Task AddAsync(Game game, DateTime createdAtUtc, CancellationToken cancellationToken = default);
}
