using RateLimiting.Abstractions;

namespace Level2.SlidingWindowLog;

/// <summary>
/// HTTP plumbing for the sliding-window-log limiter. Identical in shape to Level 1's middleware
/// (standard headers, 429 + Retry-After, /debug carve-out) — kept self-contained so this level
/// runs on its own. The only thing that changed between levels is the algorithm behind IRateLimiter.
/// </summary>
public sealed class RateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IRateLimiter _limiter;

    public RateLimitingMiddleware(RequestDelegate next, IRateLimiter limiter)
    {
        _next = next;
        _limiter = limiter;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/debug"))
        {
            await _next(context);
            // we don't want to run below logic for debug endpoint
            return;
        }

        var key = ResolveClientKey(context);
        // The decision (ALLOW/BLOCK) is logged inside the limiter itself, with its internals
        // (count in window, oldest age). The middleware just turns the decision into HTTP.
        var result = await _limiter.CheckAsync(key, context.RequestAborted);

        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["X-RateLimit-Limit"] = result.Limit.ToString();
            headers["X-RateLimit-Remaining"] = Math.Max(0, result.Remaining).ToString();
            headers["X-RateLimit-Reset"] = result.ResetsAt.ToUnixTimeSeconds().ToString();
            return Task.CompletedTask;
        });

        if (result.Allowed)
        {
            await _next(context);
            return;
        }

        var retryAfterSeconds = (int)Math.Ceiling((result.RetryAfter ?? TimeSpan.Zero).TotalSeconds);
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers["Retry-After"] = retryAfterSeconds.ToString();
        await context.Response.WriteAsJsonAsync(new
        {
            error = "rate_limit_exceeded",
            message = $"Too many requests. Retry after {retryAfterSeconds}s.",
            retryAfterSeconds
        });
    }

    private static string ResolveClientKey(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString();
        return string.IsNullOrEmpty(ip) ? "unknown" : $"ip:{ip}";
    }
}
