using System.Data.Common;
using System.Text;
using FCG.Catalog.Api.Configuration;
using FCG.Catalog.Api.Contracts.Games;
using FCG.Catalog.Api.Health;
using FCG.Catalog.Api.Services;
using FCG.Catalog.Domain.Common;
using FCG.Catalog.Infrastructure.Persistence;
using FCG.Catalog.Infrastructure.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace FCG.Catalog.Tests.Services;

public sealed class CachedGameCatalogServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hit_avoids_sql_and_absolute_expiry_reloads(bool detail)
    {
        await using var test = new Fixture();
        var game = await test.Create();
        async Task<string> Read() => detail
            ? (await test.Service.GetByIdAsync(game.Id, TestContext.Current.CancellationToken))!.Title
            : (await test.Service.ListAsync(TestContext.Current.CancellationToken)).Single().Title;
        test.Counter.Reads = 0;
        Assert.Equal("Game", await Read());
        Assert.Equal(1, test.Counter.Reads);
        test.Clock.Now = test.Clock.Now.AddSeconds(30);
        Assert.Equal("Game", await Read());
        Assert.Equal(1, test.Counter.Reads);
        // A hit must not renew absolute TTL.
        test.Clock.Now = test.Clock.Now.AddSeconds(31);
        Assert.Equal("Game", await Read());
        Assert.Equal(2, test.Counter.Reads);
    }

    [Fact]
    public async Task Crud_invalidates_list_and_affected_detail_after_commit()
    {
        await using var test = new Fixture();
        Assert.Empty(await test.Service.ListAsync(TestContext.Current.CancellationToken));
        var game = await test.Create();
        Assert.Single(await test.Service.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Game", (await test.Service.GetByIdAsync(game.Id, TestContext.Current.CancellationToken))!.Title);
        await test.Service.UpdateAsync(game.Id, new UpdateGameRequest("Updated", null, null, 80), TestContext.Current.CancellationToken);
        Assert.Equal("Updated", (await test.Service.ListAsync(TestContext.Current.CancellationToken)).Single().Title);
        Assert.Equal(80, (await test.Service.GetByIdAsync(game.Id, TestContext.Current.CancellationToken))!.Price);
        await test.Service.DeleteAsync(game.Id, TestContext.Current.CancellationToken);
        Assert.Empty(await test.Service.ListAsync(TestContext.Current.CancellationToken));
        Assert.Null(await test.Service.GetByIdAsync(game.Id, TestContext.Current.CancellationToken));
        Assert.False(test.Cache.Items.ContainsKey(CachedGameCatalogService.DetailKey(game.Id)));
    }

    [Fact]
    public async Task Failed_mutation_does_not_invalidate_cache()
    {
        await using var test = new Fixture();
        await test.Create();
        await test.Service.ListAsync(TestContext.Current.CancellationToken);
        var removals = test.Cache.Removals;
        await Assert.ThrowsAsync<DomainValidationException>(() => test.Service.UpdateAsync(Guid.NewGuid(), new UpdateGameRequest("Missing", null, null, 10), TestContext.Current.CancellationToken));
        Assert.Equal(removals, test.Cache.Removals);
        Assert.Single(await test.Service.ListAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Read_or_write_cache_failure_preserves_sql_result(bool readFailure)
    {
        await using var test = new Fixture();
        var game = await test.Create();
        test.Cache.FailRead = readFailure;
        test.Cache.FailWrite = !readFailure;
        Assert.Equal("Game", (await test.Service.GetByIdAsync(game.Id, TestContext.Current.CancellationToken))!.Title);
        Assert.Single(await test.Service.ListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(test.Cache.Items);
    }

    [Fact]
    public async Task Failed_invalidation_keeps_committed_change_and_attempts_both_keys()
    {
        await using var test = new Fixture();
        var game = await test.Create();
        await test.Service.ListAsync(TestContext.Current.CancellationToken);
        await test.Service.GetByIdAsync(game.Id, TestContext.Current.CancellationToken);
        var removals = test.Cache.Removals;
        test.Cache.FailRemove = true;
        var updated = await test.Service.UpdateAsync(game.Id, new UpdateGameRequest("Committed", null, null, 42), TestContext.Current.CancellationToken);
        Assert.Equal("Committed", updated.Title);
        Assert.Equal(removals + 2, test.Cache.Removals);
        // No distributed transaction: failed removals can leave stale entries until TTL.
        test.Clock.Now = test.Clock.Now.AddSeconds(61);
        Assert.Equal("Committed", (await test.Service.GetByIdAsync(game.Id, TestContext.Current.CancellationToken))!.Title);
        Assert.Equal("Committed", (await test.Service.ListAsync(TestContext.Current.CancellationToken)).Single().Title);
    }

    [Fact]
    public async Task Corrupt_cache_is_replaced_from_sql()
    {
        await using var test = new Fixture();
        var game = await test.Create();
        await test.Cache.SetAsync(CachedGameCatalogService.DetailKey(game.Id), Encoding.UTF8.GetBytes("not-json"), new DistributedCacheEntryOptions { AbsoluteExpiration = test.Clock.Now.AddSeconds(60) }, TestContext.Current.CancellationToken);
        Assert.Equal("Game", (await test.Service.GetByIdAsync(game.Id, TestContext.Current.CancellationToken))!.Title);
        test.Counter.Reads = 0;
        Assert.Equal("Game", (await test.Service.GetByIdAsync(game.Id, TestContext.Current.CancellationToken))!.Title);
        Assert.Equal(0, test.Counter.Reads);
    }

    [Fact]
    public async Task Caller_cancellation_does_not_fall_back_to_sql()
    {
        await using var test = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Service.ListAsync(cancellation.Token));
        Assert.Equal(0, test.Counter.Reads);
    }

    [Fact]
    public async Task Redis_failure_degrades_health_without_unhealthy_status()
    {
        await using var test = new Fixture();
        var check = new RedisCacheHealthCheck(test.Cache);
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status);
        test.Cache.FailRead = true;
        Assert.Equal(HealthStatus.Degraded, (await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly CatalogDbContext context;
        public Clock Clock { get; } = new();
        public ReadCounter Counter { get; } = new();
        public TestCache Cache { get; }
        public CachedGameCatalogService Service { get; }
        public Fixture()
        {
            connection.Open();
            context = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseSqlite(connection).AddInterceptors(Counter).Options);
            context.Database.EnsureCreated();
            Cache = new TestCache(Clock);
            Service = new CachedGameCatalogService(new GameCatalogService(new GameRepository(context), context, Clock), Cache,
                Options.Create(new CatalogCacheOptions()), Clock, NullLogger<CachedGameCatalogService>.Instance);
        }
        public Task<GameResponse> Create() => Service.CreateAsync(new CreateGameRequest("Game", "Description", "Studio", 59.90m), TestContext.Current.CancellationToken);
        public async ValueTask DisposeAsync() { await context.DisposeAsync(); await connection.DisposeAsync(); }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class ReadCounter : DbCommandInterceptor
    {
        public int Reads { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) Reads++;
            return ValueTask.FromResult(result);
        }
    }
    private sealed class TestCache(Clock clock) : IDistributedCache
    {
        public Dictionary<string, (byte[] Data, DateTimeOffset Expiry)> Items { get; } = [];
        public bool FailRead { get; set; }
        public bool FailWrite { get; set; }
        public bool FailRemove { get; set; }
        public int Removals { get; private set; }
        private static RedisException Failure() => new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Synthetic outage");
        public byte[]? Get(string key)
        {
            if (FailRead) throw Failure();
            if (Items.TryGetValue(key, out var item) && item.Expiry > clock.Now) return item.Data;
            Items.Remove(key);
            return null;
        }
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) { token.ThrowIfCancellationRequested(); return Task.FromResult(Get(key)); }
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            if (FailWrite) throw Failure();
            Items[key] = (value, options.AbsoluteExpiration ?? throw new InvalidOperationException("Absolute expiry required."));
        }
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) { token.ThrowIfCancellationRequested(); Set(key, value, options); return Task.CompletedTask; }
        public void Remove(string key) { Removals++; if (FailRemove) throw Failure(); Items.Remove(key); }
        public Task RemoveAsync(string key, CancellationToken token = default) { token.ThrowIfCancellationRequested(); Remove(key); return Task.CompletedTask; }
        public void Refresh(string key) => throw new NotSupportedException();
        public Task RefreshAsync(string key, CancellationToken token = default) => throw new NotSupportedException();
    }
}
