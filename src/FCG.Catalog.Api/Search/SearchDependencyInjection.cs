using Amazon;
using Amazon.Runtime;
using FCG.Catalog.Infrastructure.Persistence.Search;
using Microsoft.Extensions.Options;
using OpenSearch.Client;
using OpenSearch.Net;
using OpenSearch.Net.Auth.AwsSigV4;

namespace FCG.Catalog.Api.Search;

public static class SearchDependencyInjection
{
    public static IServiceCollection AddCatalogSearch(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SearchOptions>().Bind(configuration.GetSection(SearchOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<IValidateOptions<SearchOptions>, SearchOptionsValidator>();
        services.AddSingleton<IOpenSearchClient>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<SearchOptions>>().Value;
            IConnection connection;
            try
            {
                connection = options.UseAwsAuth
                    ? new AwsSigV4HttpConnection(RegionEndpoint.GetBySystemName(options.Region))
                    : new HttpConnection();
            }
            catch (AmazonClientException) { throw new SearchUnavailableException(); }
            var settings = new ConnectionSettings(new SingleNodeConnectionPool(new Uri(options.Endpoint)), connection)
                .DefaultIndex(options.IndexName)
                .RequestTimeout(TimeSpan.FromSeconds(options.RequestTimeoutSeconds))
                .MaximumRetries(0)
                .DisablePing();
            return new OpenSearchClient(settings);
        });
        services.AddSingleton<IGameSearchIndex, OpenSearchGameIndex>();
        services.AddScoped<GameSearchOutbox>();
        services.AddScoped<SearchOutboxProcessor>();
        services.AddScoped<SearchBootstrap>();
        services.AddHostedService<SearchIndexWorker>();
        return services;
    }
}

