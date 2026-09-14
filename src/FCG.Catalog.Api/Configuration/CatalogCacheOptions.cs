using System.ComponentModel.DataAnnotations;

namespace FCG.Catalog.Api.Configuration;

public sealed class CatalogCacheOptions
{
    public const string SectionName = "CatalogCache";
    [Required] public string Configuration { get; set; } = "localhost:6379";
    [Range(1, 300)] public int TtlSeconds { get; set; } = 60;
    [Range(100, 2000)] public int TimeoutMilliseconds { get; set; } = 500;
}
