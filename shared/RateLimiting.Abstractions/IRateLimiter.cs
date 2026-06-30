namespace RateLimiting.Abstractions;

/// <summary>
/// A rate limiter answers one question: "given this client key, is the next request allowed?"
///
/// The method is async (returns a Task) even though Level 1's implementation is purely in-memory
/// and synchronous. That is deliberate: Level 4 moves state into Redis, where the check becomes a
/// genuine network round-trip. Committing to an async contract now means later levels are
/// drop-in replacements rather than a breaking signature change.
/// </summary>
public interface IRateLimiter
{
    /// <summary>
    /// Evaluates whether a request for <paramref name="key"/> is allowed, and records it if so.
    /// </summary>
    /// <param name="key">
    /// The client identity to limit on: an IP, a user id, an API key, or a composite of these.
    /// Choosing this key is a design decision in its own right (see later levels).
    /// </param>
    Task<RateLimitResult> CheckAsync(string key, CancellationToken ct = default);
}
