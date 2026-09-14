using System.Text;
using FCG.Catalog.Api.Configuration;
using FCG.Catalog.Api.Consumers;
using FCG.Catalog.Api.Health;
using FCG.Catalog.Api.Messaging;
using FCG.Catalog.Api.Services;
using FCG.Catalog.Api.Shared;
using FCG.Catalog.Api.Validation;
using FCG.Catalog.Infrastructure;
using FCG.Catalog.Infrastructure.Persistence;
using FluentValidation;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using StackExchange.Redis;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

namespace FCG.Catalog.Api;

public static class DependencyInjection
{
    public static IServiceCollection AddCatalogApi(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddCatalogInfrastructure(configuration);

        services.AddOptions<CatalogCacheOptions>()
            .Bind(configuration.GetSection(CatalogCacheOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(value => !string.IsNullOrWhiteSpace(value.Configuration), "CatalogCache:Configuration is required.")
            .ValidateOnStart();
        services.AddStackExchangeRedisCache(_ => { });
        services.AddOptions<RedisCacheOptions>().Configure<IOptions<CatalogCacheOptions>>((redis, configured) =>
        {
            var settings = configured.Value;
            var connection = ConfigurationOptions.Parse(settings.Configuration);
            connection.AbortOnConnectFail = false;
            connection.ConnectRetry = 0;
            connection.ConnectTimeout = settings.TimeoutMilliseconds;
            connection.AsyncTimeout = settings.TimeoutMilliseconds;
            connection.SyncTimeout = settings.TimeoutMilliseconds;
            connection.BacklogPolicy = BacklogPolicy.FailFast;
            redis.ConfigurationOptions = connection;
        });

        services
            .AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => Encoding.UTF8.GetByteCount(options.Key) >= 32,
                "Jwt:Key must contain at least 32 bytes.")
            .ValidateOnStart();

        services
            .AddOptions<RabbitMqOptions>()
            .Bind(configuration.GetSection(RabbitMqOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => !string.IsNullOrWhiteSpace(options.Host),
                "RabbitMq:Host is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.VirtualHost),
                "RabbitMq:VirtualHost is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Username),
                "RabbitMq:Username is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Password),
                "RabbitMq:Password is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.PaymentProcessedQueue),
                "RabbitMq:PaymentProcessedQueue is required.")
            .ValidateOnStart();

        services.AddMassTransit(configurator =>
        {
            configurator.AddConsumer<PaymentProcessedConsumer>();

            configurator.UsingRabbitMq((context, rabbit) =>
            {
                var options = context
                    .GetRequiredService<IOptions<RabbitMqOptions>>()
                    .Value;

                rabbit.Host(
                    options.Host,
                    options.Port,
                    options.VirtualHost,
                    host =>
                    {
                        host.Username(options.Username);
                        host.Password(options.Password);
                    });

                rabbit.ReceiveEndpoint(options.PaymentProcessedQueue, endpoint =>
                {
                    endpoint.ConfigureConsumer<PaymentProcessedConsumer>(context);
                });
            });
        });

        services.AddValidatorsFromAssemblyContaining<CreateGameRequestValidator>();
        services.AddScoped<GameCatalogService>();
        services.AddScoped<IGameCatalogService, CachedGameCatalogService>();
        services.AddScoped<IUserLibraryService, UserLibraryService>();
        services.AddScoped<IPurchaseService, PurchaseService>();
        services.AddScoped<IOrderPlacedPublisher, MassTransitOrderPlacedPublisher>();

        services.AddProblemDetails();
        services.AddExceptionHandler<ApiExceptionHandler>();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        services
            .AddOptions<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwtOptions) =>
            {
                var jwt = jwtOptions.Value;
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(jwt.Key)),
                    NameClaimType = "name",
                    RoleClaimType = "role",
                    ClockSkew = TimeSpan.Zero
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy("Admin", policy => policy.RequireRole("Admin"));

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "FIAP Cloud Games Catalog API",
                Version = "v1",
                Description = "CRUD de jogos, solicitacao de compra e biblioteca do usuario."
            });

            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Description = "Informe apenas o JWT. O Swagger UI adiciona o prefixo Bearer automaticamente.",
                Name = "Authorization",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.Http,
                Scheme = "Bearer",
                BearerFormat = "JWT"
            });

            options.OperationFilter<AuthorizationOperationFilter>();
        });

        services
            .AddHealthChecks()
            .AddDbContextCheck<CatalogDbContext>(
                "catalog-sqlserver",
                tags: ["ready"])
            .AddCheck<RabbitMqHealthCheck>(
                "rabbitmq",
                tags: ["ready"])
            .AddCheck<RedisCacheHealthCheck>("redis-cache", tags: ["cache"]);

        return services;
    }
}
