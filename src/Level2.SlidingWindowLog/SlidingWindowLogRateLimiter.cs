using System.Collections.Concurrent;
using RateLimiting.Abstractions;

namespace Level2.SlidingWindowLog;

/// <summary>
/// Tuning knobs for the sliding-window-log limiter.
/// </summary>
/// <param name="Limit">Max requests permitted within any trailing <paramref name="Window"/>.</param>
/// <param name="Window">The length of the trailing window that slides with every request.</param>
public record SlidingWindowLogOptions(long Limit, TimeSpan Window);

/// <summary>
/// A sliding-window-LOG rate limiter.
///
/// Algorithm: keep the timestamp of every request, per key. On each request, evict timestamps
/// older than (now - window), then count what's left. If the count is below the limit, admit the
/// request and append its timestamp; otherwise reject.
///
/// Why this matters versus Level 1's fixed window:
///
///   * NO BOUNDARY BURST. There are no fixed window edges to game. The window is always the last
///     `Window` of wall-clock time, measured fresh on every request. The Level 1 scenario
///     (limit requests at :09, limit more at :11) is now correctly blocked, because the :09
///     timestamps are still inside the trailing window at :11. This is the precise-but-expensive end
///     of the accuracy/cost spectrum.
///
///   * THE COST: memory is O(requests in window) PER KEY, not O(1). A key doing 1,000 req/window
///     holds 1,000 timestamps. Under real traffic this is why a pure log is rarely used as-is —
///     it's the motivation for the sliding-window-COUNTER hybrid, and for Redis sorted sets (L4).
///
///   * It also needs a CLEANUP JOB. Fixed window's counter is one tiny struct that can sit idle
///     forever; a log for a one-shot client leaves a queue (and a dictionary key) behind. See
///     <see cref="SlidingWindowLogCleanupService"/>. Redis (L4) sidesteps this entirely with TTLs.
///
/// Like Level 1, this is correct only WITHIN A SINGLE PROCESS — the per-key lock doesn't span
/// instances. That cross-process correctness problem is still Level 4's job.
/// </summary>
public sealed class SlidingWindowLogRateLimiter : IRateLimiter
{
    private readonly SlidingWindowLogOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly long _windowMs; // window size in milliseconds

    private readonly ConcurrentDictionary<string, Log> _logs = new();

    public SlidingWindowLogRateLimiter(SlidingWindowLogOptions options, TimeProvider timeProvider)
    {
        if (options.Limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Limit must be positive.");
        if (options.Window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Window must be positive.");

        _options = options;
        _timeProvider = timeProvider;
        _windowMs = (long)options.Window.TotalMilliseconds;
    }

    public Task<RateLimitResult> CheckAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var log = _logs.GetOrAdd(key, _ => new Log());

        lock (log.SyncRoot)
        {
            EvictOlderThanWindow(log.Timestamps, nowMs);

            if (log.Timestamps.Count < _options.Limit)
            {
                log.Timestamps.Enqueue(nowMs);
                var remaining = _options.Limit - log.Timestamps.Count;

                // The current window "resets" (frees a slot) when the OLDEST entry exits it.
                var oldest = log.Timestamps.Peek();
                var resetsAt = DateTimeOffset.FromUnixTimeMilliseconds(oldest + _windowMs);
                return Task.FromResult(RateLimitResult.Allow(_options.Limit, remaining, resetsAt));
            }

            // At capacity. The next slot opens when the oldest in-window request slides out, i.e.
            // at (oldest + window). That is exactly how long the caller must wait.
            var oldestBlocked = log.Timestamps.Peek();
            var slotOpensAtMs = oldestBlocked + _windowMs;
            var retryAfter = TimeSpan.FromMilliseconds(Math.Max(0, slotOpensAtMs - nowMs));
            var resetsAtBlocked = DateTimeOffset.FromUnixTimeMilliseconds(slotOpensAtMs);
            return Task.FromResult(RateLimitResult.Block(_options.Limit, resetsAtBlocked, retryAfter));
        }
    }

    /// <summary>
    /// Drops every timestamp at or before (now - window) from the front of the queue. Because we
    /// always enqueue in time order, expired entries are always a contiguous prefix — so this is
    /// amortised O(number evicted), not O(n) per call.
    /// </summary>
    private void EvictOlderThanWindow(Queue<long> timestamps, long nowMs)
    {
        var threshold = nowMs - _windowMs;
        while (timestamps.Count > 0 && timestamps.Peek() <= threshold)
            timestamps.Dequeue();
    }

    // --- Cleanup surface (called by the hosted service; see SlidingWindowLogCleanupService) ------

    /// <summary>
    /// Evicts expired timestamps from every key and removes keys whose log is then empty. Returns
    /// the number of keys removed. This is the maintenance the fixed-window limiter never needed.
    ///
    /// Subtle concurrency note (worth saying in an interview): there is a narrow race where a
    /// request that has already fetched an entry via GetOrAdd, but is still waiting on the lock,
    /// could enqueue into an entry we remove here — orphaning that one timestamp. We use the
    /// reference-checked TryRemove overload to at least never delete an entry someone has refreshed
    /// since we emptied it. This awkwardness is precisely why Redis TTLs (L4) are so appealing:
    /// expiry becomes the datastore's job and the cleanup loop disappears.
    /// </summary>
    public int RemoveExpiredKeys()
    {
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var removed = 0;

        foreach (var (key, log) in _logs)
        {
            var isEmpty = false;
            lock (log.SyncRoot)
            {
                EvictOlderThanWindow(log.Timestamps, nowMs);
                isEmpty = log.Timestamps.Count == 0;
            }

            if (isEmpty && ((IDictionary<string, Log>)_logs).Remove(new KeyValuePair<string, Log>(key, log)))
            {
                removed++;
            }
        }

        return removed;
    }

    // --- Inspection surface (for the debug endpoint / dashboard; NOT part of IRateLimiter) -------

    public long Limit => _options.Limit;
    public TimeSpan Window => _options.Window;
    public int TrackedKeyCount => _logs.Count;

    public record KeyState(string Key, int CountInWindow, long Remaining, DateTimeOffset? OldestAt);

    public IReadOnlyList<KeyState> GetState()
    {
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var states = new List<KeyState>(_logs.Count);

        foreach (var (key, log) in _logs)
        {
            int count;
            long? oldest = null;
            lock (log.SyncRoot)
            {
                EvictOlderThanWindow(log.Timestamps, nowMs);
                count = log.Timestamps.Count;
                if (count > 0) oldest = log.Timestamps.Peek();
            }

            var remaining = Math.Max(0, _options.Limit - count);
            var oldestAt = oldest is null ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeMilliseconds(oldest.Value);
            states.Add(new KeyState(key, count, remaining, oldestAt));
        }

        return states;
    }

    /// <summary>Per-key request log guarded by <see cref="SyncRoot"/>.</summary>
    private sealed class Log
    {
        // FIFO of unix-ms timestamps, oldest at the front. This is the O(requests) memory cost.
        public Queue<long> Timestamps { get; } = new();
        public object SyncRoot { get; } = new();
    }
}
