using FCG.Catalog.Infrastructure.Persistence;
using FCG.Catalog.Infrastructure.Persistence.Search;
using Microsoft.EntityFrameworkCore;

namespace FCG.Catalog.Api.Search;

public sealed class SearchBootstrap(CatalogDbContext context, IGameSearchIndex index, TimeProvider time)
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await context.Database.MigrateAsync(cancellationToken);
        await index.EnsureMappingAsync(cancellationToken);
        // Writers/workers must be stopped by the operator for bootstrap/rebuild.
        // Page source rows to bound memory; always reuse the canonical snapshot and existing revision.
        var offset = 0;
        while (true)
        {
            var games = await context.Games.AsNoTracking().OrderBy(game => game.Id)
                .Skip(offset).Take(100).ToListAsync(cancellationToken);
            if (games.Count == 0) break;
            foreach (var game in games)
                context.SearchIndexPending.Add(SearchIndexPending.Create(game, time.GetUtcNow().UtcDateTime));
            await context.SaveChangesAsync(cancellationToken);
            context.ChangeTracker.Clear();
            offset += games.Count;
        }
    }
}

