using System.Collections.Concurrent;
using RateLimiting.Abstractions;

namespace Level7.Gateway;

/// <summary>
/// A tiny in-memory fixed-window limiter — this IS Level 1's algorithm, kept alive as the
/// degraded-mode fallback. When Redis is unavailable and the gateway is configured for
/// <see cref="DegradeMode.LocalFallback"/>, enforcement drops back to this per-process limiter.
///
/// It can't hold a global limit across instances (that's exactly why we moved to Redis at Level 4),
/// but during a Redis outage a rough per-instance cap is dramatically better than fully open — the
/// "graceful degradation" payoff of the whole roadmap. Deliberately dependency-free and allocation-
/// light so it's rock-solid on the one path where everything else has already failed.
/// </summary>
public sealed class InProcessFallbackLimiter : IRateLimiter
{
    private readonly long _limit;
    private readonly long _windowTicks;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, Counter> _counters = new();

    public InProcessFallbackLimiter(long limit, TimeSpan window, TimeProvider clock)
    {
        _limit = limit;
        _windowTicks = window.Ticks;
        _clock = clock;
    }

    public Task<RateLimitResult> CheckAsync(string key, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var windowStart = new DateTimeOffset(now.UtcTicks - now.UtcTicks % _windowTicks, TimeSpan.Zero);
        var counter = _counters.GetOrAdd(key, _ => new Counter());

        lock (counter.SyncRoot)
        {
            if (counter.WindowStart != windowStart)
            {
                counter.WindowStart = windowStart;
                counter.Count = 0;
            }

            var resetsAt = windowStart + TimeSpan.FromTicks(_windowTicks);
            if (counter.Count < _limit)
            {
                counter.Count++;
                return Task.FromResult(RateLimitResult.Allow(_limit, _limit - counter.Count, resetsAt));
            }

            return Task.FromResult(RateLimitResult.Block(_limit, resetsAt, resetsAt - now));
        }
    }

    private sealed class Counter
    {
        public long Count;
        public DateTimeOffset WindowStart = DateTimeOffset.MinValue;
        public object SyncRoot { get; } = new();
    }
}
