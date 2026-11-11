using System.Text.Json;
using Amazon.Runtime;
using FCG.Catalog.Api.Contracts.Games;
using FCG.Catalog.Infrastructure.Persistence.Search;
using Microsoft.Extensions.Options;
using OpenSearch.Client;
using OpenSearch.Net;
using HttpMethod = OpenSearch.Net.HttpMethod;

namespace FCG.Catalog.Api.Search;

// All document writes use the persisted JSON, never a later read of Games.
public sealed class OpenSearchGameIndex(IOpenSearchClient client, IOptions<SearchOptions> configured) : IGameSearchIndex
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string index = configured.Value.IndexName;
    public const int MappingVersion = 1;
    public const string MappingJson = """
    {"dynamic":"strict","_meta":{"catalogMappingVersion":1},"properties":{
      "id":{"type":"keyword"},"title":{"type":"text"},"description":{"type":"text"},
      "developer":{"type":"text"},"price":{"type":"scaled_float","scaling_factor":100},
      "isActive":{"type":"boolean"},"createdAtUtc":{"type":"date"},"updatedAtUtc":{"type":"date"}}}
    """;

    public async Task WriteAsync(SearchIndexPending pending, CancellationToken cancellationToken = default)
    {
        var path = $"{index}/_doc/{pending.GameId:D}";
        var response = await RequestAsync(HttpMethod.PUT, path, pending.PayloadJson, cancellationToken,
            new IndexRequestParameters { Version = pending.Revision, VersionType = OpenSearch.Net.VersionType.External });
        if (Succeeded(response)) return;
        if (IsExternalVersionConflict(response)) return;
        throw new SearchUnavailableException();
    }

    public static bool IsExternalVersionConflict(StringResponse response)
    {
        if (response.HttpStatusCode != 409 || string.IsNullOrWhiteSpace(response.Body)) return false;
        try
        {
            using var body = JsonDocument.Parse(response.Body);
            return body.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("type", out var type)
                && type.GetString() == "version_conflict_engine_exception";
        }
        catch (JsonException) { return false; }
    }

    public static string BuildQuery(string query) => JsonSerializer.Serialize(new
    {
        size = 20,
        query = new
        {
            @bool = new
            {
                filter = new[] { new { term = new { isActive = true } } },
                must = new[] { new { multi_match = new { query, fields = new[] { "title^3", "description", "developer" }, fuzziness = "AUTO" } } }
            }
        },
        sort = new object[] { new { _score = "desc" }, new { id = "asc" } }
    });

    public async Task<GameResponse[]> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var response = await RequestAsync(HttpMethod.POST, $"{index}/_search", BuildQuery(query), cancellationToken);
        if (!Succeeded(response)) throw new SearchUnavailableException();
        try
        {
            using var body = JsonDocument.Parse(response.Body);
            if (body.RootElement.GetProperty("timed_out").GetBoolean()
                || body.RootElement.GetProperty("_shards").GetProperty("failed").GetInt32() > 0)
                throw new SearchUnavailableException();
            return body.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray()
                .Select(hit => hit.GetProperty("_source").Deserialize<GameResponse>(JsonOptions)
                    ?? throw new SearchUnavailableException())
                .Where(game => game.IsActive).Take(20).ToArray();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new SearchUnavailableException(); }
    }

    public async Task EnsureMappingAsync(CancellationToken cancellationToken = default)
    {
        var existing = await RequestAsync(HttpMethod.GET, $"{index}/_mapping", null, cancellationToken);
        if (existing.HttpStatusCode == 404)
        {
            var created = await RequestAsync(HttpMethod.PUT, index, "{\"mappings\":" + MappingJson + "}", cancellationToken);
            if (!Succeeded(created)) throw new SearchUnavailableException();
            existing = await RequestAsync(HttpMethod.GET, $"{index}/_mapping", null, cancellationToken);
        }
        if (!Succeeded(existing)) throw new SearchUnavailableException();
        using var body = JsonDocument.Parse(existing.Body);
        using var expected = JsonDocument.Parse(MappingJson);
        if (!body.RootElement.TryGetProperty(index, out var entry)
            || !entry.TryGetProperty("mappings", out var mapping)
            || !MappingCompatible(mapping, expected.RootElement))
            throw new InvalidOperationException("Search mapping is incompatible. Use a controlled index rebuild; no automatic deletion is performed.");
    }

    public static bool MappingCompatible(JsonElement actual, JsonElement expected)
    {
        if (expected.ValueKind == JsonValueKind.Object)
            return actual.ValueKind == JsonValueKind.Object
                && expected.EnumerateObject().All(property => actual.TryGetProperty(property.Name, out var value)
                    && MappingCompatible(value, property.Value));
        return JsonElement.DeepEquals(actual, expected);
    }

    // The low-level client also considers some 404 responses successful transport calls.
    private static bool Succeeded(StringResponse response) => response.Success && response.HttpStatusCode is >= 200 and < 300;

    private async Task<StringResponse> RequestAsync(HttpMethod method, string path, string? json, CancellationToken cancellationToken,
        IRequestParameters? parameters = null)
    {
        try
        {
            return await client.LowLevel.DoRequestAsync<StringResponse>(method, path, cancellationToken,
                json is null ? null : PostData.String(json), parameters);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is OpenSearchClientException or HttpRequestException or OperationCanceledException or AmazonClientException)
        { throw new SearchUnavailableException(); }
    }
}

