using System.Collections.Concurrent;
using Level4.RedisAtomic;
using RateLimiting.Abstractions;
using StackExchange.Redis;

namespace Level5.Middleware;

/// <summary>
/// Turns a <see cref="RateLimitPolicy"/> into a concrete rate limiter, reusing the Level 4
/// Redis-backed limiters (atomic Lua, correct across instances) as the enforcement engine. One
/// limiter instance is built and cached per policy; its Redis key prefix namespaces the policy's
/// keys so dimensions never collide.
///
/// <paramref name="keyNamespace"/> lets a test (or a multi-tenant deployment) fully isolate its keys.
/// </summary>
public sealed class RedisRateLimiterProvider
{
    private readonly IConnectionMultiplexer _redis;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _keyNamespace;
    private readonly ConcurrentDictionary<string, IRateLimiter> _cache = new();

    public RedisRateLimiterProvider(
        IConnectionMultiplexer redis,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        string keyNamespace = "")
    {
        _redis = redis;
        _timeProvider = timeProvider;
        _loggerFactory = loggerFactory;
        _keyNamespace = string.IsNullOrEmpty(keyNamespace) ? "" : keyNamespace + ":";
    }

    public IRateLimiter GetLimiter(RateLimitPolicy policy) =>
        _cache.GetOrAdd(policy.Name, _ => Build(policy));

    private IRateLimiter Build(RateLimitPolicy policy)
    {
        var prefix = $"{_keyNamespace}rl:{policy.Name}:";
        var logger = _loggerFactory.CreateLogger($"RateLimit.{policy.Name}");

        return policy.Algorithm == RateLimitAlgorithm.FixedWindow
            ? new RedisFixedWindowRateLimiter(
                _redis, new RedisFixedWindowOptions(policy.Limit, policy.Window, prefix), _timeProvider, logger)
            : new RedisSlidingWindowRateLimiter(
                _redis, new RedisSlidingWindowOptions(policy.Limit, policy.Window, prefix), _timeProvider, logger);
    }
}
