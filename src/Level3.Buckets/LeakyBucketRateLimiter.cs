using System.Collections.Concurrent;
using RateLimiting.Abstractions;

namespace Level3.Buckets;

/// <summary>
/// Tuning knobs for the leaky bucket.
/// </summary>
/// <param name="Capacity">Max requests that can wait in the bucket (the queue depth).</param>
/// <param name="LeakPerSecond">The constant drain / output rate — requests released per second.</param>
public record LeakyBucketOptions(int Capacity, double LeakPerSecond);

/// <summary>
/// A leaky-bucket rate limiter, implemented via VIRTUAL SCHEDULING (a.k.a. GCRA).
///
/// Mental model: a bucket that leaks at a constant <c>LeakPerSecond</c>. Each accepted request is
/// water poured in; it drains out the bottom at the fixed rate. If the bucket is full (more than
/// <c>Capacity</c> requests already waiting), the new request overflows and is DROPPED.
///
/// The defining property versus the token bucket: the OUTPUT is perfectly smooth. No matter how
/// bursty the arrivals, requests leave at exactly <c>LeakPerSecond</c>. That's what you want when a
/// fragile downstream can only handle X/sec. The token bucket, by contrast, would let a whole burst
/// through at once.
///
/// Implementation: rather than storing a queue, we keep one timestamp per key — the "theoretical
/// arrival time" (TAT), i.e. when the NEXT request would be released. Interval T = 1/rate is the
/// spacing between releases. A request arriving at `now` is scheduled at max(TAT, now); if that
/// puts it more than (Capacity-1) intervals into the future, the bucket is full and it's dropped.
/// Accepting advances TAT by T — which is exactly why releases come out spaced T apart (smooth).
/// O(1) state per key, like the token bucket.
/// </summary>
public sealed class LeakyBucketRateLimiter : IRateLimiter
{
    private readonly LeakyBucketOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;          // T = 1 / rate, spacing between releases
    private readonly TimeSpan _burstTolerance;    // (Capacity - 1) * T, how far ahead we may schedule
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();

    public LeakyBucketRateLimiter(LeakyBucketOptions options, TimeProvider timeProvider)
    {
        if (options.Capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Capacity must be positive.");
        if (options.LeakPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "LeakPerSecond must be positive.");

        _options = options;
        _timeProvider = timeProvider;
        _interval = TimeSpan.FromSeconds(1.0 / options.LeakPerSecond);
        _burstTolerance = _interval * (options.Capacity - 1);
    }

    public Task<RateLimitResult> CheckAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var now = _timeProvider.GetUtcNow();
        var bucket = _buckets.GetOrAdd(key, _ => new Bucket());
        var limit = _options.Capacity;

        lock (bucket.SyncRoot)
        {
            // The earliest this request could be released: the queue tail, but never in the past.
            var scheduledRelease = bucket.Tat < now ? now : bucket.Tat;
            var queueDelay = scheduledRelease - now;   // how long it would wait in the bucket

            if (queueDelay <= _burstTolerance)
            {
                // Room in the bucket. Accept, and push the tail one interval further out.
                bucket.Tat = scheduledRelease + _interval;

                // How full the bucket is now, in whole requests (for the "remaining" header).
                var depthAfter = (bucket.Tat - now).TotalSeconds / _interval.TotalSeconds;
                var remaining = Math.Max(0, limit - (long)Math.Ceiling(depthAfter));
                // ResetsAt carries this request's scheduled release time — the smoothed output instant.
                return Task.FromResult(RateLimitResult.Allow(limit, remaining, scheduledRelease));
            }

            // Bucket full → overflow → drop. It drains at the fixed rate, so room opens after:
            var retryAfter = queueDelay - _burstTolerance;
            return Task.FromResult(RateLimitResult.Block(limit, now + retryAfter, retryAfter));
        }
    }

    // --- Inspection surface (dashboard / debug; NOT part of IRateLimiter) ------------------------

    public int Capacity => _options.Capacity;
    public double LeakPerSecond => _options.LeakPerSecond;
    public int TrackedKeyCount => _buckets.Count;

    public record KeyState(string Key, double QueueDepth, int Capacity);

    public IReadOnlyList<KeyState> GetState()
    {
        var now = _timeProvider.GetUtcNow();
        var states = new List<KeyState>(_buckets.Count);
        foreach (var (key, bucket) in _buckets)
        {
            lock (bucket.SyncRoot)
            {
                var ahead = bucket.Tat - now;
                var depth = ahead <= TimeSpan.Zero ? 0 : ahead.TotalSeconds / _interval.TotalSeconds;
                states.Add(new KeyState(key, Math.Min(_options.Capacity, depth), _options.Capacity));
            }
        }
        return states;
    }

    private sealed class Bucket
    {
        // Theoretical arrival time: when the next request would be released. MinValue = empty bucket.
        public DateTimeOffset Tat = DateTimeOffset.MinValue;
        public object SyncRoot { get; } = new();
    }
}
