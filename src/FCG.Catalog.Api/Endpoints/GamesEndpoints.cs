using FCG.Catalog.Api.Contracts.Games;
using FCG.Catalog.Api.Services;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;

namespace FCG.Catalog.Api.Endpoints;

public static class GamesEndpoints
{
    public static IEndpointRouteBuilder MapGamesEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/games").WithTags("Games");

        group.MapGet("/", ListAsync)
            .WithName("ListGames");

        group.MapGet("/{id:guid}", GetByIdAsync)
            .WithName("GetGameById");

        group.MapPost("/", CreateAsync)
            .RequireAuthorization("Admin")
            .WithName("CreateGame");

        group.MapPut("/{id:guid}", UpdateAsync)
            .RequireAuthorization("Admin")
            .WithName("UpdateGame");

        group.MapDelete("/{id:guid}", DeleteAsync)
            .RequireAuthorization("Admin")
            .WithName("DeleteGame");

        return app;
    }

    private static async Task<Ok<IReadOnlyList<GameResponse>>> ListAsync(
        IGameCatalogService games,
        CancellationToken cancellationToken)
    {
        var result = await games.ListAsync(cancellationToken);
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<GameResponse>, NotFound>> GetByIdAsync(
        Guid id,
        IGameCatalogService games,
        CancellationToken cancellationToken)
    {
        var result = await games.GetByIdAsync(id, cancellationToken);
        return result is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(result);
    }

    private static async Task<Created<GameResponse>> CreateAsync(
        CreateGameRequest request,
        IValidator<CreateGameRequest> validator,
        IGameCatalogService games,
        CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(request, cancellationToken);
        var result = await games.CreateAsync(request, cancellationToken);
        return TypedResults.Created($"/api/games/{result.Id}", result);
    }

    private static async Task<Ok<GameResponse>> UpdateAsync(
        Guid id,
        UpdateGameRequest request,
        IValidator<UpdateGameRequest> validator,
        IGameCatalogService games,
        CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(request, cancellationToken);
        var result = await games.UpdateAsync(id, request, cancellationToken);
        return TypedResults.Ok(result);
    }

    private static async Task<NoContent> DeleteAsync(
        Guid id,
        IGameCatalogService games,
        CancellationToken cancellationToken)
    {
        await games.DeleteAsync(id, cancellationToken);
        return TypedResults.NoContent();
    }
}
