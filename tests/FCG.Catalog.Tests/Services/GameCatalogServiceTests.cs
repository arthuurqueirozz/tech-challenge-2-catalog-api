using FCG.Catalog.Api.Contracts.Games;
using FCG.Catalog.Api.Services;
using FCG.Catalog.Infrastructure.Persistence.Repositories;
using FCG.Catalog.Infrastructure.Persistence.Search;
using FCG.Catalog.Tests.Infrastructure;

namespace FCG.Catalog.Tests.Services;

public sealed class GameCatalogServiceTests
{
    [Fact]
    public async Task Crud_flow_persists_and_hides_deleted_games()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = new SqliteCatalogFixture();
        await using var context = fixture.CreateContext();
        var repository = new GameRepository(context);
        var service = new GameCatalogService(
            repository,
            context,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 8, 12, 0, 0, TimeSpan.Zero)), new GameSearchOutbox(context));

        var created = await service.CreateAsync(
            new CreateGameRequest(
                "Game",
                "Description",
                "Studio",
                59.90m),
            cancellationToken);
        var fetched = await service.GetByIdAsync(created.Id, cancellationToken);
        var updated = await service.UpdateAsync(
            created.Id,
            new UpdateGameRequest("Updated", null, null, 79.90m),
            cancellationToken);

        await service.DeleteAsync(created.Id, cancellationToken);

        var afterDelete = await service.GetByIdAsync(created.Id, cancellationToken);
        var list = await service.ListAsync(cancellationToken);

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal("Game", fetched?.Title);
        Assert.Equal("Updated", updated.Title);
        Assert.Null(afterDelete);
        Assert.Empty(list);
    }
}
