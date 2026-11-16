using System.Globalization;
using System.Text.Json;
using FCG.Catalog.Api.Contracts.Games;
using FCG.Catalog.Api.Services;
using FCG.Catalog.Domain.Common;
using FCG.Catalog.Domain.Entities;
using FCG.Catalog.Infrastructure.Persistence;
using FCG.Catalog.Infrastructure.Persistence.Repositories;
using FCG.Catalog.Infrastructure.Persistence.Search;
using Microsoft.EntityFrameworkCore;

namespace FCG.Catalog.Tests.Infrastructure;

public sealed class SearchOutboxTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static GameCatalogService Service(CatalogDbContext context) => new(
        new GameRepository(context), context, new FixedTimeProvider(new DateTimeOffset(Now)), new GameSearchOutbox(context));

    [Fact]
    public async Task Writes_keep_distinct_immutable_snapshots_and_repeated_delete_is_noop()
    {
        await using var fixture = new SqliteCatalogFixture();
        await using var context = fixture.CreateContext();
        var service = Service(context);
        var created = await service.CreateAsync(new CreateGameRequest("First", "Description", "Studio", 19.995m), Token);
        var originalPayload = (await context.SearchIndexPending.SingleAsync(Token)).PayloadJson;
        await service.UpdateAsync(created.Id, new UpdateGameRequest("Edited", null, null, 29.9m), Token);
        await service.DeleteAsync(created.Id, Token);
        await service.DeleteAsync(created.Id, Token);

        var pending = await context.SearchIndexPending.AsNoTracking().OrderBy(item => item.Revision).ToListAsync(Token);
        Assert.Equal(3, pending.Count);
        Assert.Equal(new long[] { 1, 2, 3 }, pending.Select(item => item.Revision));
        Assert.Equal(3, pending.Select(item => item.Id).Distinct().Count());
        Assert.Equal(originalPayload, pending[0].PayloadJson);
        using var first = JsonDocument.Parse(pending[0].PayloadJson);
        using var last = JsonDocument.Parse(pending[2].PayloadJson);
        Assert.Equal("First", first.RootElement.GetProperty("title").GetString());
        Assert.Equal(20.00m, first.RootElement.GetProperty("price").GetDecimal());
        Assert.False(last.RootElement.GetProperty("isActive").GetBoolean());
        Assert.False(last.RootElement.TryGetProperty("searchRevision", out _));
    }

    [Theory]
    [InlineData("19.995", "20.00")]
    [InlineData("19.985", "19.99")]
    [InlineData("19", "19.00")]
    [InlineData("19.9", "19.90")]
    [InlineData("0.001", "0.00")]
    public async Task Projection_canonizes_sql_price_and_utc_after_database_reread(string input, string expected)
    {
        await using var fixture = new SqliteCatalogFixture();
        await using var context = fixture.CreateContext();
        var game = Game.Create(decimal.Parse(input, CultureInfo.InvariantCulture), "Game", null, null, Now);
        context.Games.Add(game);
        var original = SearchIndexPending.Create(game, Now);
        context.SearchIndexPending.Add(original);
        await context.SaveChangesAsync(Token);
        context.ChangeTracker.Clear();
        var reloaded = await context.Games.SingleAsync(Token);
        var repeated = SearchIndexPending.Create(reloaded, Now.AddMinutes(1));

        Assert.Equal(original.PayloadJson, repeated.PayloadJson);
        using var payload = JsonDocument.Parse(repeated.PayloadJson);
        Assert.Equal(expected, payload.RootElement.GetProperty("price").GetRawText());
        Assert.Equal("2026-11-03T12:00:00Z", payload.RootElement.GetProperty("createdAtUtc").GetString());
        Assert.NotEqual(original.Id, repeated.Id);
        Assert.Equal(original.Revision, repeated.Revision);
        // Re-enqueueing a delivered revision must remain possible for reconstruction.
        var outbox = new GameSearchOutbox(context);
        await outbox.CompleteAsync(original.Id, Now, Token);
        await outbox.AddAsync(reloaded, Now.AddMinutes(1), Token);
        await context.SaveChangesAsync(Token);
        Assert.Equal(2, await context.SearchIndexPending.CountAsync(Token));
    }

    [Fact]
    public async Task Pending_constraint_failure_rolls_back_game_and_all_pending_writes()
    {
        await using var fixture = new SqliteCatalogFixture();
        await using var context = fixture.CreateContext();
        var nonexistentGame = Game.Create(10m, "Missing", null, null, Now);
        context.SearchIndexPending.Add(SearchIndexPending.Create(nonexistentGame, Now));

        await Assert.ThrowsAsync<DbUpdateException>(() => Service(context)
            .CreateAsync(new CreateGameRequest("Must rollback", null, null, 20m), Token));

        await using var inspection = fixture.CreateContext();
        Assert.Empty(await inspection.Games.ToListAsync(Token));
        Assert.Empty(await inspection.SearchIndexPending.ToListAsync(Token));
    }

    [Fact]
    public async Task Concurrent_writer_conflict_rolls_back_its_pending_snapshot()
    {
        await using var fixture = new SqliteCatalogFixture();
        await using var firstContext = fixture.CreateContext();
        var created = await Service(firstContext).CreateAsync(new CreateGameRequest("Original", null, null, 20m), Token);
        await using var staleContext = fixture.CreateContext();
        await staleContext.Games.SingleAsync(Token);
        await Service(firstContext).UpdateAsync(created.Id, new UpdateGameRequest("Winner", null, null, 21m), Token);

        await Assert.ThrowsAsync<DomainConflictException>(() => Service(staleContext)
            .UpdateAsync(created.Id, new UpdateGameRequest("Lost", null, null, 22m), Token));

        await using var inspection = fixture.CreateContext();
        var game = await inspection.Games.SingleAsync(Token);
        Assert.Equal("Winner", game.Title);
        Assert.Equal(2, game.SearchRevision);
        Assert.Equal(2, await inspection.SearchIndexPending.CountAsync(Token));
        Assert.DoesNotContain(await inspection.SearchIndexPending.ToListAsync(Token), item => item.PayloadJson.Contains("Lost"));
    }

    [Fact]
    public async Task Completion_and_retry_are_conditional_on_pending_id_and_do_not_reopen_completed_items()
    {
        await using var fixture = new SqliteCatalogFixture();
        await using var context = fixture.CreateContext();
        var created = await Service(context).CreateAsync(new CreateGameRequest("Game", null, null, 20m), Token);
        await Service(context).UpdateAsync(created.Id, new UpdateGameRequest("Edited", null, null, 21m), Token);
        var items = await context.SearchIndexPending.AsNoTracking().OrderBy(item => item.Revision).ToListAsync(Token);
        var outbox = new GameSearchOutbox(context);

        Assert.Equal(1, await outbox.FailAsync(items[0].Id, "unavailable", Now.AddMinutes(1), Token));
        Assert.Equal(1, await outbox.FailAsync(items[0].Id, "timeout", Now.AddMinutes(2), Token));
        Assert.Equal(1, await outbox.CompleteAsync(items[0].Id, Now.AddMinutes(3), Token));
        Assert.Equal(0, await outbox.FailAsync(items[0].Id, "late_failure", Now.AddMinutes(4), Token));
        Assert.Equal(0, await outbox.CompleteAsync(items[0].Id, Now.AddMinutes(5), Token));

        var final = await context.SearchIndexPending.AsNoTracking().OrderBy(item => item.Revision).ToListAsync(Token);
        Assert.Equal(2, final[0].Attempts);
        Assert.Equal(Now.AddMinutes(3), final[0].CompletedAtUtc);
        Assert.Null(final[0].LastErrorCode);
        Assert.Null(final[1].CompletedAtUtc);
        Assert.Equal(0, final[1].Attempts);
    }

    [Fact]
    public async Task Eligible_query_excludes_delayed_completed_items_and_has_stable_batch_order()
    {
        await using var fixture = new SqliteCatalogFixture();
        await using var context = fixture.CreateContext();
        for (var index = 0; index < 4; index++)
        {
            await Service(context).CreateAsync(new CreateGameRequest($"Game {index}", null, null, 20m), Token);
        }
        var outbox = new GameSearchOutbox(context);
        var items = await outbox.GetEligibleAsync(Now, 10, Token);
        await outbox.FailAsync(items[0].Id, "timeout", Now.AddHours(1), Token);
        await outbox.CompleteAsync(items[1].Id, Now, Token);

        var firstBatch = await outbox.GetEligibleAsync(Now, 1, Token);
        var repeatedBatch = await outbox.GetEligibleAsync(Now, 1, Token);
        Assert.Single(firstBatch);
        Assert.Equal(firstBatch[0].Id, repeatedBatch[0].Id);
        Assert.DoesNotContain(firstBatch, item => item.Id == items[0].Id || item.Id == items[1].Id);
        Assert.Equal(3, (await outbox.GetEligibleAsync(Now.AddHours(2), 10, Token)).Count);
    }
}
