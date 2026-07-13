using StackExchange.Redis;

namespace Level4.RedisAtomic;

/// <summary>Outcome of a cost-based debit against a Redis window.</summary>
/// <param name="Allowed">True if the cost fit under the limit and was debited.</param>
/// <param name="Remaining">Units left in the window after this call.</param>
/// <param name="Ttl">Time until the window resets.</param>
public readonly record struct CostWindowResult(bool Allowed, long Remaining, TimeSpan Ttl);

/// <summary>
/// A reusable, cost-aware fixed-window counter in Redis (atomic via <c>cost_window.lua</c>). Unlike
/// the <c>IRateLimiter</c> implementations, the limit / cost / TTL are supplied PER CALL, because in
/// Level 6 they vary by the caller's plan and by the request's cost. Used for the per-minute and
/// per-day quota windows (the hot path); the durable monthly ledger lives in Postgres.
/// </summary>
public sealed class RedisCostWindow
{
    private static readonly string Script = EmbeddedScript.Load("cost_window.lua");
    private readonly IConnectionMultiplexer _redis;

    public RedisCostWindow(IConnectionMultiplexer redis) => _redis = redis;

    /// <summary>Atomically debits <paramref name="cost"/> from <paramref name="key"/> if it fits under <paramref name="limit"/>.</summary>
    public async Task<CostWindowResult> TryDebitAsync(string key, long limit, long cost, TimeSpan ttl)
    {
        var raw = (RedisResult[])(await _redis.GetDatabase().ScriptEvaluateAsync(
            Script,
            [key],
            [limit, cost, (long)ttl.TotalMilliseconds]))!;

        return new CostWindowResult(
            Allowed: (long)raw[0] == 1,
            Remaining: (long)raw[1],
            Ttl: TimeSpan.FromMilliseconds((long)raw[2]));
    }
}
