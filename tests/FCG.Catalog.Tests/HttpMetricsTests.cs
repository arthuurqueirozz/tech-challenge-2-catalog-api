using System.Text;
using FCG.Catalog.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Prometheus;

namespace FCG.Catalog.Tests;

public sealed class HttpMetricsTests
{
    [Fact]
    public async Task Metrics_group_ids_by_route_and_capture_errors_without_secrets()
    {
        var route = "/api/metrics-test/{id}";
        var firstId = Guid.NewGuid().ToString();
        var secondId = Guid.NewGuid().ToString();
        foreach (var id in new[] { firstId, secondId })
        {
            var context = Context("/api/metrics-test/" + id, route);
            await new HttpMetricsMiddleware(c => { c.Response.StatusCode = 404; return Task.CompletedTask; }).InvokeAsync(context);
        }
        var error = Context("/api/metrics-test/error", route);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new HttpMetricsMiddleware(_ => throw new InvalidOperationException("synthetic")).InvokeAsync(error));
        var handled = Context("/api/metrics-test/handled", route);
        await new HttpMetricsMiddleware(c => { c.Response.StatusCode = 401; return Task.CompletedTask; }).InvokeAsync(handled);
        var unmatched = Context("/api/random-private-path", null);
        unmatched.Request.Method = "CUSTOM-PRIVATE-METHOD";
        await new HttpMetricsMiddleware(c => { c.Response.StatusCode = 404; return Task.CompletedTask; }).InvokeAsync(unmatched);
        await new HttpMetricsMiddleware(_ => Task.CompletedTask).InvokeAsync(Context("/metrics", "/metrics"));
        await using var stream = new MemoryStream();
        await Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream, TestContext.Current.CancellationToken);
        var text = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("fcg_http_requests_total{route=\"/api/metrics-test/{id}\",method=\"GET\",status_code=\"404\"} 2", text);
        Assert.Contains("status_code=\"500\"} 1", text);
        Assert.Contains("status_code=\"401\"} 1", text);
        Assert.Contains("route=\"unmatched\",method=\"OTHER\"", text);
        Assert.Contains("fcg_http_request_duration_seconds_count", text);
        Assert.DoesNotContain(firstId, text);
        Assert.DoesNotContain(secondId, text);
        Assert.DoesNotContain("random-private-path", text);
        Assert.DoesNotContain("CUSTOM-PRIVATE-METHOD", text);
        Assert.DoesNotContain("route=\"/metrics\"", text);
    }

    private static DefaultHttpContext Context(string path, string? route)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = "GET";
        if (route is not null) context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask,
            RoutePatternFactory.Parse(route), 0, EndpointMetadataCollection.Empty, "test"));
        return context;
    }
}
