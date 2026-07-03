using RateLimiting.Abstractions;

namespace Level1.FixedWindow;

/// <summary>
/// Translates an <see cref="IRateLimiter"/> decision into HTTP: standard rate-limit headers on
/// every response, and a 429 with Retry-After when the limit is exceeded.
///
/// Header semantics (worth knowing cold for interviews):
///   X-RateLimit-Limit     - the ceiling for the window.
///   X-RateLimit-Remaining - requests left in the current window.
///   X-RateLimit-Reset     - Unix epoch SECONDS at which the window resets (GitHub/Twitter style).
///   Retry-After           - seconds to wait before retrying; only sent on a 429 (RFC 9110).
///
/// Note the distinction interviewers probe on: Retry-After answers "how long until I can try again?"
/// while X-RateLimit-Reset answers "when does my full quota come back?" For a fixed window they
/// point at the same instant, but they are not the same concept.
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
        // The dashboard and its state endpoint are developer tooling — exempt them so loading the
        // page or polling internal state doesn't consume the client's quota. (Real path/route-based
        // policies arrive in Level 5; this is a deliberately minimal carve-out.)
        if (context.Request.Path.StartsWithSegments("/debug"))
        {
            await _next(context);
            return;
        }

        var key = ResolveClientKey(context);
        // The decision (ALLOW/BLOCK) is logged inside the limiter itself, with its internals
        // (count, window). The middleware just turns the decision into HTTP.
        var result = await _limiter.CheckAsync(key, context.RequestAborted);

        // Headers must be written before the response body starts. OnStarting fires just before
        // the first byte is flushed, which guarantees they land even on the success path where a
        // downstream endpoint writes the body.
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

        // Blocked: short-circuit the pipeline. Retry-After is integer seconds, rounded up so we
        // never tell a client to retry before the window has actually reset.
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

    /// <summary>
    /// Level 1 keys on client IP. This is the weakest possible identity: it is spoofable, it
    /// collapses everyone behind a NAT/proxy into one bucket, and it ignores authenticated users.
    /// Levels 5 and 6 replace this with per-user / per-API-key / composite keys.
    /// </summary>
    private static string ResolveClientKey(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString();
        return string.IsNullOrEmpty(ip) ? "unknown" : $"ip:{ip}";
    }
}
