namespace RateLimiting.Abstractions;

/// <summary>
/// The outcome of a single rate-limit check. Immutable by design: a decision is a
/// snapshot in time, and we want callers to be able to pass it around freely.
/// </summary>
/// <param name="Allowed">True if the request may proceed; false if it should be rejected (HTTP 429).</param>
/// <param name="Limit">The ceiling for the current window (the "X-RateLimit-Limit" value).</param>
/// <param name="Remaining">How many requests remain in the current window after this one.</param>
/// <param name="ResetsAt">When the current window resets and <see cref="Remaining"/> returns to <see cref="Limit"/>.</param>
/// <param name="RetryAfter">
/// When blocked, how long the caller should wait before retrying. Null when allowed.
/// Maps directly to the HTTP "Retry-After" header.
/// </param>
public record RateLimitResult(
    bool Allowed,
    long Limit,
    long Remaining,
    DateTimeOffset ResetsAt,
    TimeSpan? RetryAfter)
{
    /// <summary>Convenience factory for an allowed decision.</summary>
    public static RateLimitResult Allow(long limit, long remaining, DateTimeOffset resetsAt) =>
        new(true, limit, remaining, resetsAt, RetryAfter: null);

    /// <summary>Convenience factory for a blocked decision.</summary>
    public static RateLimitResult Block(long limit, DateTimeOffset resetsAt, TimeSpan retryAfter) =>
        new(false, limit, Remaining: 0, resetsAt, retryAfter);
}
