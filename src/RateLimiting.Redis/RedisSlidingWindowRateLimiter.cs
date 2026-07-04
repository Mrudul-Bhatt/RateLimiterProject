using Microsoft.Extensions.Logging;
using RateLimiting.Abstractions;
using StackExchange.Redis;

namespace Level4.RedisAtomic;

public record RedisSlidingWindowOptions(long Limit, TimeSpan Window, string KeyPrefix = "rl:sliding:");

/// <summary>
/// The distributed version of Level 2's sliding-window log. Instead of a per-process
/// <c>Queue&lt;long&gt;</c>, the request timestamps live in a Redis SORTED SET (member = unique id,
/// score = timestamp). One Lua script does ZREMRANGEBYSCORE (evict) -> ZCARD (count) ->
/// ZADD (record) atomically. See <c>Scripts/sliding_window.lua</c>.
///
/// Two Level-2 problems vanish:
///  - Cross-instance correctness: all instances share one sorted set.
///  - The cleanup job: PEXPIRE on the key means idle keys self-expire.
///
/// Clock note: we pass the app clock (TimeProvider) as `now`, which keeps tests deterministic. In
/// production across many instances you'd instead read Redis server time (redis.call('TIME')) inside
/// the script to avoid clock skew between app hosts — a one-line change, called out in the docs.
/// </summary>
public sealed class RedisSlidingWindowRateLimiter : IRateLimiter
{
    private static readonly string Script = EmbeddedScript.Load("sliding_window.lua");

    private readonly IConnectionMultiplexer _redis;
    private readonly RedisSlidingWindowOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger? _logger;

    public RedisSlidingWindowRateLimiter(
        IConnectionMultiplexer redis,
        RedisSlidingWindowOptions options,
        TimeProvider timeProvider,
        ILogger? logger = null)
    {
        if (options.Limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Limit must be positive.");
        if (options.Window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Window must be positive.");

        _redis = redis;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<RateLimitResult> CheckAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var db = _redis.GetDatabase();
        var redisKey = _options.KeyPrefix + key;
        var now = _timeProvider.GetUtcNow();
        var nowMillis = now.ToUnixTimeMilliseconds();
        var windowMillis = (long)_options.Window.TotalMilliseconds;
        // Unique member so identical-timestamp requests don't collide in the sorted set.
        var member = $"{nowMillis}-{Guid.NewGuid():N}";

        var raw = (RedisResult[])(await db.ScriptEvaluateAsync(
            Script,
            new RedisKey[] { redisKey },
            new RedisValue[] { nowMillis, windowMillis, _options.Limit, member }))!;

        var allowed = (long)raw[0] == 1;
        var remaining = (long)raw[1];
        var retryAfter = TimeSpan.FromMilliseconds((long)raw[2]);

        if (allowed)
        {
            var resetsAt = now + _options.Window; // conservative: full window from now
            _logger?.LogInformation(
                "REDIS-SLIDING ALLOW {Key}  remaining={Remaining}/{Limit}",
                key, remaining, _options.Limit);
            return RateLimitResult.Allow(_options.Limit, remaining, resetsAt);
        }

        _logger?.LogWarning(
            "REDIS-SLIDING BLOCK {Key}  limit={Limit}  retryAfter={RetryAfter}",
            key, _options.Limit, retryAfter);
        return RateLimitResult.Block(_options.Limit, now + retryAfter, retryAfter);
    }
}
