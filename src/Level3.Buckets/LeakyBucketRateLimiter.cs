using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger? _logger;
    private readonly TimeSpan _interval;          // T = 1 / rate, spacing between releases
    private readonly TimeSpan _burstTolerance;    // (Capacity - 1) * T, how far ahead we may schedule
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();

    // Logger is optional so unit tests can construct the limiter without a DI container.
    public LeakyBucketRateLimiter(LeakyBucketOptions options, TimeProvider timeProvider, ILogger? logger = null)
    {
        if (options.Capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Capacity must be positive.");
        if (options.LeakPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "LeakPerSecond must be positive.");

        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _interval = TimeSpan.FromSeconds(1.0 / options.LeakPerSecond); // 1 req process time
        _burstTolerance = _interval * (options.Capacity - 1); // process time for 5 (capacity) requests
    }

    public Task<RateLimitResult> CheckAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        // Numbers below trace capacity=5, rate=2/s  =>  _interval = 500ms, _burstTolerance = 4*500 = 2000ms.
        // Walking a burst that arrives all at now=0 (Tat starts at MinValue = empty bucket).
        var now = _timeProvider.GetUtcNow();
        var bucket = _buckets.GetOrAdd(key, _ => new Bucket());
        var limit = _options.Capacity;

        lock (bucket.SyncRoot)
        {
            // The earliest this request could be released: the queue tail, but never in the past.
            // req1: Tat=MinValue<now => scheduledRelease=now=0.   req2: Tat=500>now => scheduledRelease=500.
            // req3: 1000.  req4: 1500.  req5: 2000.  req6: 2500.
            var scheduledRelease = bucket.Tat < now ? now : bucket.Tat;
            // How long this request would wait in the bucket. req1:0  req2:500  req3:1000  req4:1500  req5:2000  req6:2500 (ms)
            var queueDelay = scheduledRelease - now;

            if (queueDelay <= _burstTolerance)   // <= 2000ms? req1..req5: yes (req5 is 2000, equal => still in).
            {
                // Room in the bucket. Accept, and push the tail one interval (500ms) further out.
                // req1: Tat 0->500.  req2: 500->1000.  req3: 1000->1500.  req4: 1500->2000.  req5: 2000->2500.
                bucket.Tat = scheduledRelease + _interval;

                // How full the bucket is now, in whole requests (for the "remaining" header).
                // depthAfter = (Tat-now)/interval.  req1:(500-0)/500=1  req2:2  req3:3  req4:4  req5:5
                var depthAfter = (bucket.Tat - now).TotalSeconds / _interval.TotalSeconds;
                // remaining = 5 - ceil(depth).  req1:4  req2:3  req3:2  req4:1  req5:0
                var remaining = Math.Max(0, limit - (long)Math.Ceiling(depthAfter));

                _logger?.LogInformation(
                    "LEAKY ALLOW {Key}  release@+{QueueDelayMs:F0}ms  depth={Depth:F1}/{Capacity}  remaining={Remaining}",
                    key, queueDelay.TotalMilliseconds, depthAfter, _options.Capacity, remaining);
                // ResetsAt carries this request's scheduled release time — the smoothed output instant.
                // releases land at 0, 500, 1000, 1500, 2000 ms => evenly spaced by 500ms = smooth 2/s output.
                return Task.FromResult(RateLimitResult.Allow(limit, remaining, scheduledRelease));
            }

            // Bucket full => overflow => drop. req6: queueDelay=2500 > 2000 => here.
            // Room opens after (queueDelay - burstTolerance).  req6: 2500 - 2000 = 500ms => retry in half a second.
            var retryAfter = queueDelay - _burstTolerance;

            _logger?.LogWarning(
                "LEAKY DROP  {Key}  queueDelay={QueueDelayMs:F0}ms > tolerance={ToleranceMs:F0}ms  retryAfter={RetryAfter}",
                key, queueDelay.TotalMilliseconds, _burstTolerance.TotalMilliseconds, retryAfter);
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
