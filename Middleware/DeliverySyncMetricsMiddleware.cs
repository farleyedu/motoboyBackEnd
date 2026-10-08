using System.Diagnostics;
using APIBack.Services;

namespace APIBack.Middleware;

public sealed class DeliverySyncMetricsMiddleware(RequestDelegate next, ILogger<DeliverySyncMetricsMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, DeliverySyncMetrics metrics)
    {
        var operation = context.Request.Path.Value?.ToLowerInvariant() switch
        {
            "/api/v2/motoboys/me/session/heartbeat" => "heartbeat",
            "/api/v2/motoboys/me/session/location" => "location",
            "/api/v2/motoboys/me/session/location/batch" => "location.batch",
            "/api/v2/motoboys/me/session/queue" => "queue",
            _ => null
        };
        if (operation == null) { await next(context); return; }
        var started = Stopwatch.GetTimestamp();
        try { await next(context); }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var status = context.RequestAborted.IsCancellationRequested ? 499 : context.Response.StatusCode;
            metrics.RecordRequest(operation, status, elapsed);
            if (elapsed >= 2000)
                logger.LogWarning("Sincronizacao lenta: {Operation}, {Status}, {ElapsedMs:F0} ms, traceId {TraceId}.",
                    operation, status, elapsed, context.TraceIdentifier);
        }
    }
}
