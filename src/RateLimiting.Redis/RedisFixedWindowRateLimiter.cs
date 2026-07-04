using Microsoft.Extensions.Logging;
using RateLimiting.Abstractions;
using StackExchange.Redis;

namespace Level4.RedisAtomic;

public record RedisFixedWindowOptions(long Limit, TimeSpan Window, string KeyPrefix = "rl:fixed:");

/// <summary>
/// A fixed-window limiter whose counter lives in REDIS, not process memory. This is the whole point
/// of Level 4: the limit is now correct across N application instances, because they share one
/// counter and the read-check-increment happens atomically inside a Lua script on the Redis server.
///
/// Levels 1–3 were all "correct only within a single process." Two instances each kept their own
/// dictionary, so the effective limit was limit × instances. Moving state to Redis fixes that; using
/// Lua (rather than app-side GET + INCR) removes the cross-instance TOCTOU race. See
/// <c>Scripts/fixed_window.lua</c>.
///
/// Bonus: the key's TTL both resets the window AND expires idle keys — so unlike Level 2 there is no
/// cleanup job to run.
/// </summary>
public sealed class RedisFixedWindowRateLimiter : IRateLimiter
{
    private static readonly string Script = EmbeddedScript.Load("fixed_window.lua");

    private readonly IConnectionMultiplexer _redis;
    private readonly RedisFixedWindowOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger? _logger;

    public RedisFixedWindowRateLimiter(
        IConnectionMultiplexer redis,
        RedisFixedWindowOptions options,
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
        var windowMillis = (long)_options.Window.TotalMilliseconds;

        // One round trip. The script does GET -> check -> INCR -> PEXPIRE atomically on the server.
        var raw = (RedisResult[])(await db.ScriptEvaluateAsync(
            Script,
            [redisKey],
            [_options.Limit, windowMillis]))!;

        var allowed = (long)raw[0] == 1;
        var remaining = (long)raw[1];
        var ttl = TimeSpan.FromMilliseconds((long)raw[2]);
        var resetsAt = _timeProvider.GetUtcNow() + ttl;

        if (allowed)
        {
            _logger?.LogInformation(
                "REDIS-FIXED ALLOW {Key}  remaining={Remaining}/{Limit}  ttl={Ttl}",
                key, remaining, _options.Limit, ttl);
            return RateLimitResult.Allow(_options.Limit, remaining, resetsAt);
        }

        _logger?.LogWarning(
            "REDIS-FIXED BLOCK {Key}  limit={Limit}  retryAfter={Ttl}",
            key, _options.Limit, ttl);
        return RateLimitResult.Block(_options.Limit, resetsAt, ttl);
    }
}
