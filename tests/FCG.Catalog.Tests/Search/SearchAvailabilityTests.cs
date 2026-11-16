using Amazon.Runtime;
using FCG.Catalog.Api.Contracts.Games;
using FCG.Catalog.Api.Endpoints;
using FCG.Catalog.Api.Search;
using FCG.Catalog.Infrastructure.Persistence.Search;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenSearch.Client;
using OpenSearch.Net;

namespace FCG.Catalog.Tests.Search;

public sealed class SearchAvailabilityTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public async Task Blank_query_does_not_resolve_an_unavailable_dependency(string? query)
    {
        var resolutions = 0;
        var services = new ServiceCollection();
        services.AddSingleton<IGameSearchIndex>(_ =>
        {
            resolutions++;
            throw new InvalidOperationException("Dependency must not be resolved for blank input.");
        });
        await using var provider = services.BuildServiceProvider();

        var response = await SearchEndpoints.SearchAsync(query, provider, Token);

        Assert.Equal(400, Assert.IsAssignableFrom<IStatusCodeHttpResult>(response).StatusCode);
        Assert.Equal(0, resolutions);
    }

    [Fact]
    public async Task Dependency_resolution_failure_returns_service_unavailable()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGameSearchIndex>(_ => throw new SearchUnavailableException());
        await using var provider = services.BuildServiceProvider();

        var response = await SearchEndpoints.SearchAsync("game", provider, Token);

        Assert.Equal(503, Assert.IsAssignableFrom<IStatusCodeHttpResult>(response).StatusCode);
    }

    [Fact]
    public async Task Dependency_resolution_can_recover_after_failure_and_receives_trimmed_input()
    {
        var resolutions = 0;
        string? observedQuery = null;
        var services = new ServiceCollection();
        services.AddSingleton<IGameSearchIndex>(_ =>
        {
            if (++resolutions == 1) throw new SearchUnavailableException();
            return new QueryIndex((query, cancellationToken) =>
            {
                observedQuery = query;
                Assert.Equal(Token, cancellationToken);
                return Task.FromResult(Array.Empty<GameResponse>());
            });
        });
        await using var provider = services.BuildServiceProvider();

        var failed = await SearchEndpoints.SearchAsync("game", provider, Token);
        var recovered = await SearchEndpoints.SearchAsync("  game  ", provider, Token);

        Assert.Equal(503, Assert.IsAssignableFrom<IStatusCodeHttpResult>(failed).StatusCode);
        Assert.Equal(200, Assert.IsAssignableFrom<IStatusCodeHttpResult>(recovered).StatusCode);
        Assert.Equal(2, resolutions);
        Assert.Equal("game", observedQuery);
    }

    [Fact]
    public async Task Programming_error_is_not_reported_as_dependency_unavailability()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGameSearchIndex>(_ => throw new InvalidOperationException("Synthetic programming error."));
        await using var provider = services.BuildServiceProvider();

        await Assert.ThrowsAsync<InvalidOperationException>(() => SearchEndpoints.SearchAsync("game", provider, Token));
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_service_unavailable()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancellation.Cancel();
        var services = new ServiceCollection();
        services.AddSingleton<IGameSearchIndex>(new QueryIndex((_, token) => Task.FromCanceled<GameResponse[]>(token)));
        await using var provider = services.BuildServiceProvider();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SearchEndpoints.SearchAsync("game", provider, cancellation.Token));
    }

    [Fact]
    public async Task Credential_failure_during_transport_is_reported_as_search_unavailable()
    {
        var connection = new InMemoryConnection();
        var settings = new ConnectionSettings(new SingleNodeConnectionPool(new Uri("http://localhost:9200")), connection)
            .DisablePing().MaximumRetries(0)
            .OnRequestCompleted(_ => throw new AmazonClientException("Synthetic credential refresh failure."));
        var index = new OpenSearchGameIndex(new OpenSearchClient(settings), Options.Create(new SearchOptions
        {
            Endpoint = "http://localhost:9200",
            UseAwsAuth = false
        }));

        await Assert.ThrowsAsync<SearchUnavailableException>(() => index.SearchAsync("game", Token));
    }

    private sealed class QueryIndex(Func<string, CancellationToken, Task<GameResponse[]>> search) : IGameSearchIndex
    {
        public Task<GameResponse[]> SearchAsync(string query, CancellationToken cancellationToken = default) => search(query, cancellationToken);
        public Task WriteAsync(SearchIndexPending pending, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EnsureMappingAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
