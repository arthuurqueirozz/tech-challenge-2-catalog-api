using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace FCG.Catalog.Api.Health;

public sealed class RedisCacheHealthCheck(IDistributedCache cache) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await cache.GetAsync("fcg:catalog:v1:health", cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception error) when (error is RedisException or TimeoutException)
        {
            return HealthCheckResult.Degraded("Redis unavailable; catalog reads fall back to SQL.");
        }
    }
}
