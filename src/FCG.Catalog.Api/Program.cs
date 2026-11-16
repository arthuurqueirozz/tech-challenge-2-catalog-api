using FCG.Catalog.Api;
using FCG.Catalog.Api.Endpoints;
using FCG.Catalog.Infrastructure;
using FCG.Catalog.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Serilog;
using Prometheus;
using FCG.Catalog.Api.Search;

var migrate = args.Contains("--migrate", StringComparer.Ordinal);
var searchBootstrap = args.Contains("--search-bootstrap", StringComparer.Ordinal);
if (migrate && searchBootstrap)
{
    throw new ArgumentException("Use either --migrate or --search-bootstrap, not both.");
}
var hostArgs = args.Where(argument => argument is not "--migrate" and not "--search-bootstrap").ToArray();
var builder = WebApplication.CreateBuilder(hostArgs);

if (migrate)
{
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddCatalogInfrastructure(builder.Configuration);
    await using var migrationApp = builder.Build();
    await migrationApp.Services.InitialiseCatalogDatabaseAsync();
    return;
}

builder.Host.UseSerilog((context, services, configuration) =>
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

builder.Services.AddCatalogApi(builder.Configuration);
builder.Services.AddCatalogSearch(builder.Configuration);

await using var app = builder.Build();

if (searchBootstrap)
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<SearchBootstrap>().RunAsync();
    return;
}

var buildSha = Environment.GetEnvironmentVariable("FCG_BUILD_SHA") ?? "development";
if (buildSha != "development" && (buildSha.Length != 40 || !buildSha.All(Uri.IsHexDigit)))
{
    throw new InvalidOperationException("FCG_BUILD_SHA must be a full Git commit SHA or development.");
}
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers["X-Build-Sha"] = buildSha;
        return Task.CompletedTask;
    });
    await next(context);
});

app.UseRouting();
app.UseMiddleware<HttpMetricsMiddleware>();
app.UseExceptionHandler();
app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => TypedResults.Ok(new
{
    service = "FCG Catalog API",
    status = "running"
}))
.WithName("GetServiceStatus");

app.MapHealthChecks("/health");
app.MapMetrics();
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});
app.MapGamesEndpoints();
app.MapLibraryEndpoints();
app.MapSearchEndpoints();

await app.RunAsync();

public partial class Program;
