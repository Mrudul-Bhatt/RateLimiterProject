using RateLimiting.Abstractions;

namespace Level5.Middleware;

public sealed record RateLimitBypassOptions(string? InternalToken);

/// <summary>
/// Applies a CHAIN of rate-limit policies to each request and fails fast on the first violated
/// dimension. This is what real API gateways do: cap per IP AND per user AND per API key at once.
///
/// Flow:
///   1. Whitelist bypass — an internal service token skips limiting entirely.
///   2. For each policy in order, if it applies (KeySelector returns non-null), check it against
///      its Redis limiter. On the first BLOCK, short-circuit with 429 naming that dimension.
///   3. If all applicable policies allow, set headers from the MOST CONSTRAINED dimension (the one
///      with the least remaining — that's the limit a client will hit first) and continue.
///
/// A subtlety worth stating in an interview: because we increment each dimension's counter as we go,
/// a request rejected by a LATER dimension has already "charged" the earlier ones. That's inherent
/// to fail-fast multi-dimension limiting; you minimise the waste by ordering cheapest / most-likely-
/// to-fail first (see the registry).
/// </summary>
public sealed class CompositeRateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RateLimitPolicyRegistry _registry;
    private readonly RedisRateLimiterProvider _provider;
    private readonly RateLimitBypassOptions _bypass;
    private readonly ILogger<CompositeRateLimitingMiddleware> _logger;

    public CompositeRateLimitingMiddleware(
        RequestDelegate next,
        RateLimitPolicyRegistry registry,
        RedisRateLimiterProvider provider,
        RateLimitBypassOptions bypass,
        ILogger<CompositeRateLimitingMiddleware> logger)
    {
        _next = next;
        _registry = registry;
        _provider = provider;
        _bypass = bypass;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Developer tooling is never limited.
        if (context.Request.Path.StartsWithSegments("/debug"))
        {
            await _next(context);
            return;
        }

        // 1. Whitelist bypass for trusted internal callers.
        if (!string.IsNullOrEmpty(_bypass.InternalToken) &&
            context.Request.Headers["X-Internal-Token"] == _bypass.InternalToken)
        {
            _logger.LogInformation("BYPASS  internal service token — skipping all rate limits.");
            await _next(context);
            return;
        }

        RateLimitResult? mostConstrained = null;
        string? mostConstrainedDimension = null;

        // 2. Evaluate the chain in order, fail-fast.
        foreach (var policy in _registry.Chain)
        {
            var keyValue = policy.KeySelector(context);
            if (keyValue is null)
                continue; // dimension not applicable to this request

            var limiter = _provider.GetLimiter(policy);
            var result = await limiter.CheckAsync(keyValue, context.RequestAborted);

            if (!result.Allowed)
            {
                _logger.LogWarning(
                    "BLOCK   dimension={Dimension} key={Key} limit={Limit}",
                    policy.Name, keyValue, result.Limit);
                await WriteRejection(context, policy.Name, result);
                return;
            }

            // Track the tightest applicable dimension for the success-path headers.
            if (mostConstrained is null || result.Remaining < mostConstrained.Remaining)
            {
                mostConstrained = result;
                mostConstrainedDimension = policy.Name;
            }
        }

        // 3. All applicable dimensions allowed. Report the tightest one.
        if (mostConstrained is not null)
            WriteHeaders(context, mostConstrainedDimension!, mostConstrained);

        await _next(context);
    }

    private static void WriteHeaders(HttpContext context, string dimension, RateLimitResult result)
    {
        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            h["X-RateLimit-Limit"] = result.Limit.ToString();
            h["X-RateLimit-Remaining"] = Math.Max(0, result.Remaining).ToString();
            h["X-RateLimit-Reset"] = result.ResetsAt.ToUnixTimeSeconds().ToString();
            h["X-RateLimit-Dimension"] = dimension; // which dimension these numbers describe
            return Task.CompletedTask;
        });
    }

    private static async Task WriteRejection(HttpContext context, string dimension, RateLimitResult result)
    {
        var retryAfterSeconds = (int)Math.Ceiling((result.RetryAfter ?? TimeSpan.Zero).TotalSeconds);
        var h = context.Response.Headers;
        h["X-RateLimit-Limit"] = result.Limit.ToString();
        h["X-RateLimit-Remaining"] = "0";
        h["X-RateLimit-Reset"] = result.ResetsAt.ToUnixTimeSeconds().ToString();
        h["X-RateLimit-Dimension"] = dimension;
        h["Retry-After"] = retryAfterSeconds.ToString();

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "rate_limit_exceeded",
            dimension,                    // which limit you hit (ip / anon / user / apikey)
            limit = result.Limit,
            retryAfterSeconds
        });
    }
}
