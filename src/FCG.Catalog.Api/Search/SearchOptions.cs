using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace FCG.Catalog.Api.Search;

public sealed class SearchOptions
{
    public const string SectionName = "Search";
    public string Endpoint { get; set; } = "";
    public string IndexName { get; set; } = "catalog-games-v1";
    public string Region { get; set; } = "";
    public bool UseAwsAuth { get; set; } = true;
    [Range(1, 60)] public int RequestTimeoutSeconds { get; set; } = 5;
    [Range(1, 100)] public int BatchSize { get; set; } = 20;
    [Range(1, 60)] public int PollIntervalSeconds { get; set; } = 2;
    [Range(1, 3600)] public int MaxRetryDelaySeconds { get; set; } = 60;
}

public sealed class SearchOptionsValidator(IHostEnvironment environment) : IValidateOptions<SearchOptions>
{
    public ValidateOptionsResult Validate(string? name, SearchOptions options)
    {
        var errors = new List<string>();
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != "http" && endpoint.Scheme != "https")
            || endpoint.AbsolutePath != "/" || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            errors.Add("Search:Endpoint must be an HTTP(S) origin without credentials, path or query.");
        if (!Regex.IsMatch(options.IndexName, "^[a-z0-9][a-z0-9_-]{0,254}$") || options.IndexName is "." or "..")
            errors.Add("Search:IndexName must be a lowercase index name.");
        if (!environment.IsDevelopment() && (!options.UseAwsAuth || endpoint?.Scheme != "https"))
            errors.Add("Search requires AWS authentication and HTTPS outside Development.");
        if (options.UseAwsAuth && (endpoint?.Scheme != "https" || string.IsNullOrWhiteSpace(options.Region)))
            errors.Add("Search AWS authentication requires HTTPS and a region.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

