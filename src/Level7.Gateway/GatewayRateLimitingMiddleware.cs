using System.Diagnostics;

namespace Level7.Gateway;

/// <summary>
/// Edge enforcement: this middleware runs at the GATEWAY, before the request is forwarded to any
/// backend (YARP). Rate limiting happens once, at the front door, with zero per-service overhead —
/// contrast Levels 1–6 where each app enforced for itself.
///
/// For every request it: opens a trace span, asks the <see cref="ResilientRateLimiter"/> for a
/// decision, records metrics, writes standard headers, and — if blocked — returns 429 WITHOUT
/// calling next() (so the backend is never touched).
/// </summary>
public sealed class GatewayRateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ResilientRateLimiter _limiter;

    public GatewayRateLimitingMiddleware(RequestDelegate next, ResilientRateLimiter limiter)
    {
        _next = next;
        _limiter = limiter;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/metrics") ||
            context.Request.Path.StartsWithSegments("/debug"))
        {
            await _next(context);
            return;
        }

        var key = ResolveKey(context);
        var endpoint = EndpointLabel(context.Request.Path);

        using var activity = GatewayTelemetry.ActivitySource.StartActivity("rate_limit.check", ActivityKind.Internal);
        activity?.SetTag("rate_limit.key", key);
        activity?.SetTag("rate_limit.endpoint", endpoint);

        var decision = await _limiter.EvaluateAsync(key, context.RequestAborted);
        var result = decision.Result;
        var resultLabel = result.Allowed ? "allowed" : "blocked";

        // --- observe ---
        activity?.SetTag("rate_limit.result", resultLabel);
        activity?.SetTag("rate_limit.source", decision.Source);
        activity?.SetTag("rate_limit.remaining", result.Remaining);
        activity?.SetTag("rate_limit.degraded", decision.Degraded);
        GatewayTelemetry.RequestsTotal.WithLabels(endpoint, resultLabel, decision.Source).Inc();

        // --- standard headers (skip the sentinel -1 values a fail-open decision carries) ---
        var h = context.Response.Headers;
        if (result.Limit >= 0) h["X-RateLimit-Limit"] = result.Limit.ToString();
        if (result.Remaining >= 0) h["X-RateLimit-Remaining"] = Math.Max(0, result.Remaining).ToString();
        h["X-RateLimit-Source"] = decision.Source;               // redis | local_fallback | fail_open | fail_closed

        if (result.Allowed)
        {
            await _next(context); // forward to the backend
            return;
        }

        // Blocked at the edge — the backend is never reached.
        var reason = decision.Source == "fail_closed" ? "fail_closed" : "over_limit";
        GatewayTelemetry.RejectedTotal.WithLabels(endpoint, reason).Inc();

        var retryAfter = (int)Math.Ceiling((result.RetryAfter ?? TimeSpan.FromSeconds(1)).TotalSeconds);
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        h["Retry-After"] = retryAfter.ToString();
        await context.Response.WriteAsJsonAsync(new
        {
            error = "rate_limit_exceeded", source = decision.Source, degraded = decision.Degraded, retryAfterSeconds = retryAfter
        });
    }

    private static string ResolveKey(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString();
        return string.IsNullOrEmpty(ip) ? "unknown" : $"ip:{ip}";
    }

    // Keep metric cardinality low: label by the first path segment, not the full path.
    private static string EndpointLabel(PathString path)
    {
        var s = path.Value ?? "/";
        var seg = s.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return seg.Length == 0 ? "/" : "/" + seg[0];
    }
}
