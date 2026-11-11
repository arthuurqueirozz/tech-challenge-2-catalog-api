using FCG.Catalog.Api.Contracts.Library;
using FCG.Catalog.Api.Contracts.Purchases;
using FCG.Catalog.Api.Services;
using FCG.Catalog.Api.Shared;
using FCG.Catalog.Domain.Common;
using Microsoft.AspNetCore.Http.HttpResults;

namespace FCG.Catalog.Api.Endpoints;

public static class LibraryEndpoints
{
    public static IEndpointRouteBuilder MapLibraryEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/me/library")
            .RequireAuthorization()
            .WithTags("Library");

        group.MapGet("/games", GetMyGamesAsync)
            .WithName("GetMyLibrary");

        group.MapPost("/games/{gameId:guid}", RequestPurchaseAsync)
            .WithName("RequestGamePurchase");

        return app;
    }

    private static async Task<Ok<IReadOnlyList<LibraryGameResponse>>> GetMyGamesAsync(
        HttpContext httpContext,
        IUserLibraryService library,
        CancellationToken cancellationToken)
    {
        var userId = httpContext.User.GetUserId()
            ?? throw new DomainValidationException("Authenticated user id is invalid.");

        var result = await library.GetByUserIdAsync(userId, cancellationToken);
        return TypedResults.Ok(result);
    }

    private static async Task<Accepted<PurchaseResponse>> RequestPurchaseAsync(
        Guid gameId,
        HttpContext httpContext,
        IPurchaseService purchases,
        CancellationToken cancellationToken)
    {
        var userId = httpContext.User.GetUserId()
            ?? throw new DomainValidationException("Authenticated user id is invalid.");
        var userEmail = httpContext.User.GetEmail()
            ?? throw new DomainValidationException("Authenticated user email is invalid.");

        var result = await purchases.RequestPurchaseAsync(
            userId,
            userEmail,
            gameId,
            cancellationToken);

        return TypedResults.Accepted($"/api/me/library/games", result);
    }
}
