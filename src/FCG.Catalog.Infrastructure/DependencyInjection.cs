using FCG.Catalog.Domain.Abstractions;
using FCG.Catalog.Infrastructure.Persistence;
using FCG.Catalog.Infrastructure.Persistence.Repositories;
using FCG.Catalog.Infrastructure.Persistence.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FCG.Catalog.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddCatalogInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration
            .GetConnectionString("CatalogDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'CatalogDatabase' is not configured.");

        services.AddDbContext<CatalogDbContext>(options =>
            options.UseSqlServer(
                connectionString,
                sql => sql.EnableRetryOnFailure(
                    maxRetryCount: 10,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorNumbersToAdd: null)));

        services.AddScoped<IGameRepository, GameRepository>();
        services.AddScoped<IGameSearchOutbox, GameSearchOutbox>();
        services.AddScoped<IUserGameRepository, UserGameRepository>();
        services.AddScoped<IUnitOfWork>(
            provider => provider.GetRequiredService<CatalogDbContext>());

        return services;
    }
}
