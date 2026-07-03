using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RateLimiting.Abstractions;

namespace Level3.Buckets;

/// <summary>
/// Tuning knobs for the token bucket.
/// </summary>
/// <param name="Capacity">Max tokens the bucket holds — this is the largest instantaneous burst.</param>
/// <param name="RefillPerSecond">Tokens added per second — this is the sustained average rate.</param>
public record TokenBucketOptions(double Capacity, double RefillPerSecond);

/// <summary>
/// A token-bucket rate limiter.
///
/// Mental model: a bucket holds up to <c>Capacity</c> tokens and refills at <c>RefillPerSecond</c>.
/// Every request costs 1 token. If a token is available it's spent and the request is allowed;
/// otherwise the request is rejected. The bucket starts FULL, so a freshly-idle client can fire a
/// burst of up to <c>Capacity</c> requests instantly — then is throttled to the refill rate.
///
/// This is the defining property: token bucket ABSORBS BURSTS (up to capacity) while capping the
/// long-run average at the refill rate. It's the most common API limiter (AWS, Stripe-style).
///
/// Refill is LAZY: we don't run a timer adding tokens. Instead, on each request we compute how many
/// tokens *would* have accrued since we last touched this key — (now - lastRefill) * rate — and add
/// them, capped at capacity. O(1) state per key: one double + one timestamp. No background job, no
/// per-request log (contrast Level 2). Still single-process; distribution is Level 4.
/// </summary>
public sealed class TokenBucketRateLimiter : IRateLimiter
{
    private readonly TokenBucketOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();

    // Logger is optional so unit tests can construct the limiter without a DI container.
    public TokenBucketRateLimiter(TokenBucketOptions options, TimeProvider timeProvider, ILogger? logger = null)
    {
        if (options.Capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Capacity must be positive.");
        if (options.RefillPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "RefillPerSecond must be positive.");

        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task<RateLimitResult> CheckAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var now = _timeProvider.GetUtcNow();
        var bucket = _buckets.GetOrAdd(key, _ => new Bucket(_options.Capacity, now));
        var limit = (long)_options.Capacity;

        lock (bucket.SyncRoot)
        {
            Refill(bucket, now);

            if (bucket.Tokens >= 1.0)
            {
                var before = bucket.Tokens;
                bucket.Tokens -= 1.0;
                var remaining = (long)Math.Floor(bucket.Tokens);
                // "Resets" (back to full) after the missing tokens refill.
                var secondsToFull = (_options.Capacity - bucket.Tokens) / _options.RefillPerSecond;
                var resetsAt = now.AddSeconds(secondsToFull);

                _logger?.LogInformation(
                    "TOKEN ALLOW {Key}  tokens {Before:F2}->{After:F2}/{Capacity:F0}  remaining={Remaining}",
                    key, before, bucket.Tokens, _options.Capacity, remaining);
                return Task.FromResult(RateLimitResult.Allow(limit, remaining, resetsAt));
            }

            // Not enough for one token. Time until we accrue the fraction we're short.
            var deficit = 1.0 - bucket.Tokens;
            var retryAfter = TimeSpan.FromSeconds(deficit / _options.RefillPerSecond);

            _logger?.LogWarning(
                "TOKEN BLOCK {Key}  tokens={Tokens:F2}/{Capacity:F0}  retryAfter={RetryAfter}",
                key, bucket.Tokens, _options.Capacity, retryAfter);
            return Task.FromResult(RateLimitResult.Block(limit, now + retryAfter, retryAfter));
        }
    }

    /// <summary>Lazily accrue tokens for the elapsed time, capped at capacity.</summary>
    private void Refill(Bucket bucket, DateTimeOffset now)
    {
        // total seconds can be fractional ex: 5.6, so tokens can be fractional like 4.3
        var elapsedSeconds = (now - bucket.LastRefill).TotalSeconds;
        if (elapsedSeconds <= 0) return;

        bucket.Tokens = Math.Min(_options.Capacity, bucket.Tokens + elapsedSeconds * _options.RefillPerSecond);
        bucket.LastRefill = now;
    }

    // --- Inspection surface (dashboard / debug; NOT part of IRateLimiter) ------------------------

    public double Capacity => _options.Capacity;
    public double RefillPerSecond => _options.RefillPerSecond;
    public int TrackedKeyCount => _buckets.Count;

    public record KeyState(string Key, double Tokens, double Capacity);

    public IReadOnlyList<KeyState> GetState()
    {
        var now = _timeProvider.GetUtcNow();
        var states = new List<KeyState>(_buckets.Count);
        foreach (var (key, bucket) in _buckets)
        {
            lock (bucket.SyncRoot)
            {
                Refill(bucket, now);
                states.Add(new KeyState(key, bucket.Tokens, _options.Capacity));
            }
        }
        return states;
    }

    private sealed class Bucket
    {
        public Bucket(double tokens, DateTimeOffset lastRefill)
        {
            Tokens = tokens;
            LastRefill = lastRefill;
        }

        public double Tokens;
        public DateTimeOffset LastRefill;
        public object SyncRoot { get; } = new();
    }
}
