using FCG.Catalog.Api.Contracts.Games;
using FCG.Catalog.Domain.Abstractions;
using FCG.Catalog.Domain.Common;
using FCG.Catalog.Domain.Entities;

namespace FCG.Catalog.Api.Services;

public sealed class GameCatalogService(
    IGameRepository gameRepository,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    IGameSearchOutbox searchOutbox)
    : IGameCatalogService
{
    public async Task<GameResponse> CreateAsync(
        CreateGameRequest request,
        CancellationToken cancellationToken = default)
    {
        var game = Game.Create(
            request.Price,
            request.Title,
            request.Description,
            request.Developer,
            timeProvider.GetUtcNow().UtcDateTime);

        await gameRepository.AddAsync(game, cancellationToken);
        await searchOutbox.AddAsync(game, game.CreatedAtUtc, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return GameMappings.ToResponse(game);
    }

    public async Task<GameResponse?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var game = await gameRepository.GetByIdAsync(id, cancellationToken);
        return game is null || !game.IsActive
            ? null
            : GameMappings.ToResponse(game);
    }

    public async Task<IReadOnlyList<GameResponse>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var games = await gameRepository.ListAsync(cancellationToken);
        return games.Select(GameMappings.ToResponse).ToList();
    }

    public async Task<GameResponse> UpdateAsync(
        Guid id,
        UpdateGameRequest request,
        CancellationToken cancellationToken = default)
    {
        var game = await gameRepository.GetByIdAsync(id, cancellationToken)
            ?? throw new DomainValidationException("Game not found.");

        if (!game.IsActive)
        {
            throw new DomainValidationException("Game not found.");
        }

        game.Update(
            request.Price,
            request.Title,
            request.Description,
            request.Developer,
            timeProvider.GetUtcNow().UtcDateTime);

        await searchOutbox.AddAsync(game, game.UpdatedAtUtc!.Value, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return GameMappings.ToResponse(game);
    }

    public async Task DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var game = await gameRepository.GetByIdAsync(id, cancellationToken)
            ?? throw new DomainValidationException("Game not found.");

        if (!game.IsActive)
        {
            return;
        }

        game.Deactivate(timeProvider.GetUtcNow().UtcDateTime);
        await searchOutbox.AddAsync(game, game.UpdatedAtUtc!.Value, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
