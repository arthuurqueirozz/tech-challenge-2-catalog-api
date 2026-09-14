using System.Text.Json;
using FCG.Catalog.Api.Configuration;
using FCG.Catalog.Api.Contracts.Games;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace FCG.Catalog.Api.Services;

// Only public catalog reads pass through this decorator. Purchases still read SQL directly.
public sealed class CachedGameCatalogService(
    GameCatalogService source,
    IDistributedCache cache,
    IOptions<CatalogCacheOptions> options,
    TimeProvider clock,
    ILogger<CachedGameCatalogService> logger) : IGameCatalogService
{
    public const string ListKey = "fcg:catalog:v1:games:active:title-asc";
    public static string DetailKey(Guid id) => $"fcg:catalog:v1:game:{id:D}";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<IReadOnlyList<GameResponse>> ListAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(ListKey, source.ListAsync, cancellationToken);

    public Task<GameResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        ReadAsync(DetailKey(id), ct => source.GetByIdAsync(id, ct), cancellationToken);

    public async Task<GameResponse> CreateAsync(CreateGameRequest request, CancellationToken cancellationToken = default)
    {
        var result = await source.CreateAsync(request, cancellationToken);
        await InvalidateAsync(result.Id);
        return result;
    }

    public async Task<GameResponse> UpdateAsync(Guid id, UpdateGameRequest request, CancellationToken cancellationToken = default)
    {
        var result = await source.UpdateAsync(id, request, cancellationToken);
        await InvalidateAsync(id);
        return result;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await source.DeleteAsync(id, cancellationToken);
        await InvalidateAsync(id);
    }

    private async Task<T> ReadAsync<T>(string key, Func<CancellationToken, Task<T>> fetch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var bytes = await cache.GetAsync(key, ct);
            if (bytes is not null)
            {
                var value = JsonSerializer.Deserialize<T>(bytes, JsonOptions);
                if (value is not null) return value;
            }
        }
        catch (Exception error) when (IsCacheFailure(error))
        {
            LogFailure("read", error);
            return await fetch(ct);
        }
        catch (JsonException)
        {
            logger.LogWarning("Invalid catalog cache payload; reloading from SQL.");
        }

        // Anchor expiry before SQL: a slow query must not extend stale data lifetime.
        var expires = clock.GetUtcNow().AddSeconds(options.Value.TtlSeconds);
        var result = await fetch(ct);
        if (result is not null && clock.GetUtcNow() < expires)
        {
            try
            {
                await cache.SetAsync(key, JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions),
                    new DistributedCacheEntryOptions { AbsoluteExpiration = expires }, ct);
            }
            catch (Exception error) when (IsCacheFailure(error)) { LogFailure("write", error); }
        }
        return result;
    }

    private async Task InvalidateAsync(Guid id)
    {
        // SQL already committed. Attempt both removals even if the client disconnected
        // or one cache operation failed; Redis timeouts bound the extra work.
        foreach (var key in new[] { ListKey, DetailKey(id) })
        {
            try { await cache.RemoveAsync(key, CancellationToken.None); }
            catch (Exception error) when (IsCacheFailure(error)) { LogFailure("invalidate", error); }
        }
    }

    private static bool IsCacheFailure(Exception error) => error is RedisException or TimeoutException;
    private void LogFailure(string operation, Exception error) =>
        logger.LogWarning("Catalog cache {Operation} failed ({ErrorType}); SQL remains authoritative.", operation, error.GetType().Name);
}
