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
    private readonly ILogger<RateLimitingMiddleware> _logger;

    public RateLimitingMiddleware(
        RequestDelegate next,
        IRateLimiter limiter,
        ILogger<RateLimitingMiddleware> logger)
    {
        _next = next;
        _limiter = limiter;
        _logger = logger;
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
            _logger.LogInformation(
                "ALLOW  {Key} {Method} {Path}  remaining={Remaining}/{Limit}",
                key, context.Request.Method, context.Request.Path, result.Remaining, result.Limit);
            await _next(context);
            return;
        }

        _logger.LogWarning(
            "BLOCK  {Key} {Method} {Path}  limit={Limit} retryAfter={RetryAfter}",
            key, context.Request.Method, context.Request.Path, result.Limit, result.RetryAfter);

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
