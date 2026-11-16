using FCG.Catalog.Api.Search;
using Microsoft.AspNetCore.Mvc;

namespace FCG.Catalog.Api.Endpoints;

public static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/search", SearchAsync).AllowAnonymous().WithTags("Search").WithName("SearchGames")
            .Produces<Contracts.Games.GameResponse[]>(200).ProducesProblem(400).ProducesProblem(503);
        return app;
    }

    internal static async Task<IResult> SearchAsync(
        [FromQuery] string? q,
        [FromServices] IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Results.Problem(statusCode: 400, title: "A nonblank q parameter is required.");
        try
        {
            // Resolve the dependency only after validating input, within its availability boundary.
            var search = services.GetRequiredService<IGameSearchIndex>();
            return Results.Ok(await search.SearchAsync(q.Trim(), cancellationToken));
        }
        catch (SearchUnavailableException)
        { return Results.Problem(statusCode: 503, title: "Search is temporarily unavailable. Try again later."); }
    }
}

