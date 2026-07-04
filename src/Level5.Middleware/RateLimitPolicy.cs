namespace Level5.Middleware;

public enum RateLimitAlgorithm
{
    SlidingWindow,
    FixedWindow
}

/// <summary>
/// One rate-limiting DIMENSION: how to derive the client key for it, and its limit/window/algorithm.
///
/// The <see cref="KeySelector"/> returns the key value for this dimension, or <c>null</c> to mean
/// "this dimension does not apply to this request" (e.g. the user dimension for an anonymous caller,
/// or the api-key dimension when no key was sent). Returning null makes the middleware SKIP the
/// policy for that request — that is how a single ordered chain adapts to anonymous vs authenticated
/// vs api-key traffic without branching logic.
/// </summary>
public sealed class RateLimitPolicy
{
    public required string Name { get; init; }
    public required Func<HttpContext, string?> KeySelector { get; init; }
    public required long Limit { get; init; }
    public required TimeSpan Window { get; init; }
    public RateLimitAlgorithm Algorithm { get; init; } = RateLimitAlgorithm.SlidingWindow;
}
