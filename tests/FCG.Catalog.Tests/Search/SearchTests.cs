using System.Text;
using System.Text.Json;
using FCG.Catalog.Api.Contracts.Games;
using FCG.Catalog.Api.Search;
using FCG.Catalog.Domain.Entities;
using FCG.Catalog.Infrastructure.Persistence.Search;
using FCG.Catalog.Infrastructure.Persistence;
using FCG.Catalog.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using OpenSearch.Client;
using OpenSearch.Net;

namespace FCG.Catalog.Tests.Search;

public sealed class SearchTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static SearchOptions Settings => new() { Endpoint = "http://localhost:9200", UseAwsAuth = false };

    [Theory]
    [InlineData("Production", "http://localhost:9200", false, "", false)]
    [InlineData("Production", "https://search.example.com", false, "", false)]
    [InlineData("Production", "https://search.example.com", true, "us-east-2", true)]
    [InlineData("Production", "https://search.example.com", true, "", false)]
    [InlineData("Development", "http://localhost:9200", false, "", true)]
    [InlineData("Development", "http://localhost:9200", true, "us-east-2", false)]
    [InlineData("Development", "http://user:password@localhost:9200", false, "", false)]
    [InlineData("Development", "http://localhost:9200/some-path", false, "", false)]
    [InlineData("Development", "ftp://localhost", false, "", false)]
    public void Configuration_enforces_environment_and_transport(string environment, string endpoint, bool auth, string region, bool valid)
    {
        var options = new SearchOptions { Endpoint = endpoint, UseAwsAuth = auth, Region = region };
        var result = new SearchOptionsValidator(new EnvironmentStub(environment)).Validate(null, options);
        Assert.Equal(valid, result.Succeeded);
    }

    [Fact]
    public void Query_uses_full_text_fuzzy_and_active_filter_without_interpreting_user_text_as_syntax()
    {
        const string input = "title:foo \"bar\" * OR baz";
        using var query = JsonDocument.Parse(OpenSearchGameIndex.BuildQuery(input));
        var root = query.RootElement;
        Assert.Equal(20, root.GetProperty("size").GetInt32());
        var boolean = root.GetProperty("query").GetProperty("bool");
        Assert.True(boolean.GetProperty("filter")[0].GetProperty("term").GetProperty("isActive").GetBoolean());
        var match = boolean.GetProperty("must")[0].GetProperty("multi_match");
        Assert.Equal(input, match.GetProperty("query").GetString());
        Assert.Equal("AUTO", match.GetProperty("fuzziness").GetString());
        Assert.Equal("title^3", match.GetProperty("fields")[0].GetString());
        Assert.Equal("desc", root.GetProperty("sort")[0].GetProperty("_score").GetString());
        Assert.Equal("asc", root.GetProperty("sort")[1].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Writer_sends_exact_persisted_payload_with_id_and_external_revision()
    {
        var game = Game.Create(19.995m, "Original", null, null, Now);
        var pending = SearchIndexPending.Create(game, Now);
        game.Update(20, "Later", null, null, Now.AddMinutes(1));
        byte[]? sent = null;
        Uri? uri = null;
        var index = Index("{}", 201, details => { sent = details.RequestBodyInBytes; uri = details.Uri; });
        await index.WriteAsync(pending, Token);
        Assert.Equal(pending.PayloadJson, Encoding.UTF8.GetString(sent!));
        Assert.Contains($"/{game.Id:D}", uri!.AbsolutePath);
        Assert.Contains("version=1", uri.Query);
        Assert.Contains("version_type=external", uri.Query);
    }

    [Theory]
    [InlineData(409, "{\"error\":{\"type\":\"version_conflict_engine_exception\"}}", true)]
    [InlineData(409, "{\"error\":{\"type\":\"conflict_exception\"}}", false)]
    [InlineData(500, "{\"error\":{\"type\":\"version_conflict_engine_exception\"}}", false)]
    [InlineData(409, "{\"error\":\"version_conflict_engine_exception\"}", false)]
    [InlineData(409, "invalid-json", false)]
    [InlineData(404, "{\"error\":{\"type\":\"index_not_found_exception\"}}", false)]
    public async Task Only_specific_external_version_conflict_is_delivered(int status, string response, bool delivered)
    {
        var pending = SearchIndexPending.Create(Game.Create(10m, "Game", null, null, Now), Now);
        var index = Index(response, status);
        if (delivered) await index.WriteAsync(pending, Token);
        else await Assert.ThrowsAsync<SearchUnavailableException>(() => index.WriteAsync(pending, Token));
    }

    [Theory]
    [InlineData(404, "{\"error\":{\"type\":\"index_not_found_exception\"}}")]
    [InlineData(503, "{}")]
    [InlineData(200, "{\"timed_out\":true,\"_shards\":{\"failed\":0},\"hits\":{\"hits\":[]}}")]
    [InlineData(200, "{\"timed_out\":false,\"_shards\":{\"failed\":1},\"hits\":{\"hits\":[]}}")]
    [InlineData(200, "{}")]
    public async Task Search_rejects_missing_index_engine_timeout_partial_shard_failure_and_invalid_response(int status, string response)
    {
        await Assert.ThrowsAsync<SearchUnavailableException>(() => Index(response, status).SearchAsync("game", Token));
    }

    [Fact]
    public async Task Search_returns_public_dto_array_or_empty_and_never_exposes_transport_fields()
    {
        var game = Game.Create(10m, "Game", null, null, Now);
        var source = SearchIndexPending.Create(game, Now).PayloadJson;
        var body = "{\"timed_out\":false,\"_shards\":{\"failed\":0},\"hits\":{\"hits\":[{\"_version\":42,\"_source\":" + source + "}]}}";
        var results = await Index(body, 200).SearchAsync("game", Token);
        Assert.Equal(game.Id, Assert.Single(results).Id);
        Assert.DoesNotContain("_version", JsonSerializer.Serialize(results));
        var empty = await Index("{\"timed_out\":false,\"_shards\":{\"failed\":0},\"hits\":{\"hits\":[]}}", 200).SearchAsync("missing", Token);
        Assert.Empty(empty);
    }

    [Fact]
    public async Task Existing_mapping_requires_version_and_field_compatibility()
    {
        var mapping = "{\"catalog-games-v1\":{\"mappings\":" + OpenSearchGameIndex.MappingJson + "}}";
        await Index(mapping, 200).EnsureMappingAsync(Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Index(mapping.Replace("\"keyword\"", "\"text\""), 200).EnsureMappingAsync(Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Index(mapping.Replace("\"catalogMappingVersion\":1", "\"catalogMappingVersion\":2"), 200).EnsureMappingAsync(Token));
    }

    [Fact]
    public async Task Batch_failure_is_retried_with_bounded_delay_while_other_item_completes()
    {
        await using var fixture = new SqliteCatalogFixture();
        await using var context = fixture.CreateContext();
        var first = Game.Create(10, "Failed", null, null, Now);
        var second = Game.Create(20, "Success", null, null, Now);
        context.Games.AddRange(first, second);
        var failed = SearchIndexPending.Create(first, Now);
        var success = SearchIndexPending.Create(second, Now);
        context.SearchIndexPending.AddRange(failed, success);
        await context.SaveChangesAsync(Token);
        var writer = new FakeIndex((item, _) => item.Id == failed.Id ? Task.FromException(new SearchUnavailableException()) : Task.CompletedTask);
        var options = Settings;
        options.MaxRetryDelaySeconds = 5;
        var processor = new SearchOutboxProcessor(new GameSearchOutbox(context), writer,
            Options.Create(options), new FixedTimeProvider(new DateTimeOffset(Now)), NullLogger<SearchOutboxProcessor>.Instance);
        Assert.Equal(2, await processor.ProcessBatchAsync(Token));
        var rows = await context.SearchIndexPending.AsNoTracking().ToDictionaryAsync(row => row.Id, Token);
        Assert.Null(rows[failed.Id].CompletedAtUtc);
        Assert.Equal(1, rows[failed.Id].Attempts);
        Assert.Equal(Now.AddSeconds(1), rows[failed.Id].NextAttemptAtUtc);
        Assert.Equal("search_delivery_failed", rows[failed.Id].LastErrorCode);
        Assert.Equal(Now, rows[success.Id].CompletedAtUtc);
        Assert.Equal(0, await processor.ProcessBatchAsync(Token)); // Failed item not eligible until its delay expires.
    }

    [Fact]
    public async Task Cancellation_does_not_record_delivery_failure()
    {
        await using var fixture = new SqliteCatalogFixture();
        await using var context = fixture.CreateContext();
        var game = Game.Create(10, "Canceled", null, null, Now);
        context.Games.Add(game);
        context.SearchIndexPending.Add(SearchIndexPending.Create(game, Now));
        await context.SaveChangesAsync(Token);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var writer = new FakeIndex((_, token) => { cancel.Cancel(); return Task.FromCanceled(token); });
        var processor = new SearchOutboxProcessor(new GameSearchOutbox(context), writer,
            Options.Create(Settings), new FixedTimeProvider(new DateTimeOffset(Now)), NullLogger<SearchOutboxProcessor>.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessBatchAsync(cancel.Token));
        var row = await context.SearchIndexPending.AsNoTracking().SingleAsync(Token);
        Assert.Equal(0, row.Attempts);
        Assert.Null(row.CompletedAtUtc);
    }

    [Theory]
    [InlineData(0, 60, 1)]
    [InlineData(3, 60, 8)]
    [InlineData(20, 60, 60)]
    [InlineData(int.MaxValue, 3600, 3600)]
    public void Retry_delay_is_bounded_even_for_large_attempts(int attempts, int maximum, double expected) =>
        Assert.Equal(expected, SearchOutboxProcessor.RetryDelaySeconds(attempts, maximum));

    [Fact]
    public async Task Worker_creates_and_disposes_a_database_scope_for_each_bounded_batch()
    {
        await using var fixture = new SqliteCatalogFixture();
        await using (var seed = fixture.CreateContext())
        {
            foreach (var title in new[] { "First batch", "Second batch" })
            {
                var game = Game.Create(10, title, null, null, Now);
                seed.Games.Add(game);
                seed.SearchIndexPending.Add(SearchIndexPending.Create(game, Now));
            }
            await seed.SaveChangesAsync(Token);
        }
        var contexts = new List<CatalogDbContext>();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        var options = Settings;
        options.BatchSize = 1;
        options.PollIntervalSeconds = 1;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(new DateTimeOffset(Now)));
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<IGameSearchIndex>(new FakeIndex((_, _) =>
        {
            if (Interlocked.Increment(ref writes) == 2) delivered.TrySetResult();
            return Task.CompletedTask;
        }));
        services.AddScoped(_ => { var context = fixture.CreateContext(); contexts.Add(context); return context; });
        services.AddScoped<GameSearchOutbox>();
        services.AddScoped<SearchOutboxProcessor>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var worker = new SearchIndexWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options), NullLogger<SearchIndexWorker>.Instance);
        await worker.StartAsync(Token);
        try { await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token); }
        finally { await worker.StopAsync(CancellationToken.None); }
        Assert.Equal(2, writes);
        Assert.True(contexts.Count >= 2);
        Assert.NotSame(contexts[0], contexts[1]);
        foreach (var context in contexts)
            Assert.Throws<ObjectDisposedException>(() => context.Games.ToList());
    }

    private static OpenSearchGameIndex Index(string response, int status, Action<IApiCallDetails>? callback = null)
    {
        var connection = new InMemoryConnection(Encoding.UTF8.GetBytes(response), status);
        var settings = new ConnectionSettings(new SingleNodeConnectionPool(new Uri("http://localhost:9200")), connection)
            .DisableDirectStreaming().DisablePing().MaximumRetries(0);
        if (callback is not null) settings.OnRequestCompleted(callback);
        return new OpenSearchGameIndex(new OpenSearchClient(settings), Options.Create(Settings));
    }

    private sealed class FakeIndex(Func<SearchIndexPending, CancellationToken, Task> write) : IGameSearchIndex
    {
        public Task WriteAsync(SearchIndexPending item, CancellationToken cancellationToken = default) => write(item, cancellationToken);
        public Task<GameResponse[]> SearchAsync(string query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EnsureMappingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class EnvironmentStub(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

