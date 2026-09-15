using System.Diagnostics;
using Prometheus;

namespace FCG.Catalog.Api;

public sealed class HttpMetricsMiddleware(RequestDelegate next)
{
    private static readonly string[] Labels = ["route", "method", "status_code"];
    private static readonly Counter Requests = Metrics.CreateCounter("fcg_http_requests_total",
        "Completed business HTTP requests, including client and server errors.", new CounterConfiguration { LabelNames = Labels });
    private static readonly Histogram Duration = Metrics.CreateHistogram("fcg_http_request_duration_seconds",
        "Business HTTP request duration in seconds.", new HistogramConfiguration
        {
            LabelNames = Labels,
            Buckets = [.005, .01, .025, .05, .1, .25, .5, 1, 2.5, 5, 10, 30]
        });

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api")) { await next(context); return; }
        // Capture matched template before exception handling can clear the endpoint.
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
        var method = context.Request.Method switch
        {
            "GET" or "POST" or "PUT" or "DELETE" or "PATCH" or "HEAD" or "OPTIONS" => context.Request.Method,
            _ => "OTHER"
        };
        var started = Stopwatch.GetTimestamp();
        var failed = false;
        try { await next(context); }
        catch { failed = true; throw; }
        finally
        {
            var status = failed ? "500" : context.Response.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Requests.WithLabels(route, method, status).Inc();
            Duration.WithLabels(route, method, status).Observe(Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }
}
