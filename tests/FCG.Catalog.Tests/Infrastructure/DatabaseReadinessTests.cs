using FCG.Catalog.Api;
using FCG.Catalog.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FCG.Catalog.Tests.Infrastructure;

public sealed class DatabaseReadinessTests
{
    [Theory]
    [InlineData(false, false, HealthStatus.Unhealthy)]
    [InlineData(true, false, HealthStatus.Healthy)]
    [InlineData(true, true, HealthStatus.Healthy)]
    public async Task Readiness_RequiresKnownMigrationsWithoutWritingSchema(
        bool migrated, bool futureMigration, HealthStatus expected)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<CatalogDbContext>().UseSqlite(connection).Options;
        await using var context = new CatalogDbContext(options);
        if (migrated)
        {
            await context.Database.ExecuteSqlRawAsync(
                "CREATE TABLE __EFMigrationsHistory (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT NOT NULL)", cancellationToken);
            foreach (var migration in context.Database.GetMigrations())
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO __EFMigrationsHistory VALUES ({migration}, {"10.0.14"})", cancellationToken);
            if (futureMigration)
                await context.Database.ExecuteSqlRawAsync(
                    "INSERT INTO __EFMigrationsHistory VALUES ('20990101000000_FutureCompatible', '10.0.14')", cancellationToken);
        }
        var before = await SchemaAsync(connection, cancellationToken);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:CatalogDatabase"] = "not-a-valid-connection-string",
                ["RabbitMq:Username"] = "synthetic-user",
                ["RabbitMq:Password"] = "synthetic-password"
            }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCatalogApi(configuration);
        services.AddScoped(_ => new CatalogDbContext(options));
        await using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(registration => registration.Name == "catalog-sqlserver", cancellationToken);

        Assert.Equal(expected, result.Status);
        Assert.Contains("ready", result.Entries["catalog-sqlserver"].Tags);
        Assert.Equal(before, await SchemaAsync(connection, cancellationToken));
    }

    private static async Task<string> SchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(sql, ';') FROM (SELECT sql FROM sqlite_master ORDER BY name)";
        return (await command.ExecuteScalarAsync(cancellationToken))?.ToString() ?? string.Empty;
    }
}
