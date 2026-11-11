using FCG.Catalog.Infrastructure.Persistence.Search;
using Microsoft.Extensions.Options;

namespace FCG.Catalog.Api.Search;

public sealed class SearchOutboxProcessor(GameSearchOutbox outbox, IGameSearchIndex index,
    IOptions<SearchOptions> configured, TimeProvider time, ILogger<SearchOutboxProcessor> logger)
{
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        var options = configured.Value;
        var items = await outbox.GetEligibleAsync(time.GetUtcNow().UtcDateTime, options.BatchSize, cancellationToken);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await index.WriteAsync(item, cancellationToken);
                await outbox.CompleteAsync(item.Id, time.GetUtcNow().UtcDateTime, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                // Log only a fixed diagnostic code and ID; never payload, query, endpoint or SDK body.
                logger.LogWarning("Search delivery failed for pending item {PendingId}", item.Id);
                try
                {
                    await outbox.FailAsync(item.Id, "search_delivery_failed",
                        time.GetUtcNow().UtcDateTime.AddSeconds(RetryDelaySeconds(item.Attempts, options.MaxRetryDelaySeconds)),
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception) { logger.LogWarning("Search retry metadata could not be saved for {PendingId}", item.Id); }
            }
        }
        return items.Count;
    }

    public static double RetryDelaySeconds(int attempts, int maximum) =>
        Math.Min(maximum, Math.Pow(2, Math.Clamp(attempts, 0, 30)));
}

public sealed class SearchIndexWorker(IServiceScopeFactory scopes, IOptions<SearchOptions> configured,
    ILogger<SearchIndexWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<SearchOutboxProcessor>().ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Search batch failed; pending items will be retried"); }
            try { await Task.Delay(TimeSpan.FromSeconds(configured.Value.PollIntervalSeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}

