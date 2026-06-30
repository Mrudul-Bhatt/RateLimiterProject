using System.Collections.Concurrent;
using RateLimiting.Abstractions;

namespace Level1.FixedWindow;

/// <summary>
/// Tuning knobs for the fixed-window limiter.
/// </summary>
/// <param name="Limit">Max requests permitted per window, per key.</param>
/// <param name="Window">
/// The window length. Windows are aligned to the wall clock (the unix epoch), so for a 1-minute
/// window every client shares the boundaries :00, :01, :02 ... regardless of when they first arrive.
/// This is the classic "fixed window counter" and the variant that exhibits the boundary burst.
/// </param>
public record FixedWindowOptions(long Limit, TimeSpan Window);

/// <summary>
/// A fixed-window counter rate limiter.
///
/// Algorithm: each key gets a counter and a window-start timestamp. While we're inside the
/// window we increment and compare against the limit. When the window has elapsed, the next
/// access lazily resets the counter to zero and starts a fresh window.
///
/// Two properties of this implementation are intentional and worth stating out loud:
///
///   1. O(1) memory per key. We store a single counter, not a list of timestamps. That is the
///      headline advantage of fixed window over the sliding-window-log we build in Level 2.
///
///   2. It is correct only WITHIN A SINGLE PROCESS. The per-key lock below serializes concurrent
///      requests inside *this* app instance. Run two instances behind a load balancer, each with
///      its own ConcurrentDictionary, and a client can spend its full limit against each one — so
///      the effective limit is (limit x instances). That race is precisely what Level 4 fixes by
///      moving the counter into Redis and making the read-modify-write atomic there.
///
/// And the famous flaw — the boundary burst — is demonstrated by a test, not hidden. See the
/// "Demonstrates_boundary_burst" test in the test project.
/// </summary>
public sealed class FixedWindowRateLimiter : IRateLimiter
{
    private readonly FixedWindowOptions _options;
    private readonly TimeProvider _timeProvider;

    // One entry per client key. ConcurrentDictionary handles concurrent *adds* safely; the
    // per-entry lock (inside Counter) handles concurrent *mutation* of an existing counter.
    private readonly ConcurrentDictionary<string, Counter> _counters = new();

    public FixedWindowRateLimiter(FixedWindowOptions options, TimeProvider timeProvider)
    {
        if (options.Limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Limit must be positive.");
        if (options.Window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Window must be positive.");

        _options = options;
        _timeProvider = timeProvider;
    }

    public Task<RateLimitResult> CheckAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var now = _timeProvider.GetUtcNow();
        var currentWindowStart = AlignToWindow(now);
        var counter = _counters.GetOrAdd(key, _ => new Counter());

        // Lock the single counter for this key. The critical section is tiny (a comparison and an
        // increment), so contention is low. We deliberately do NOT use a global lock — that would
        // serialize unrelated keys against each other and kill throughput.
        lock (counter.SyncRoot)
        {
            // Lazy reset: if we've crossed into a new aligned window since this key was last seen,
            // zero the counter and adopt the new window. No background timer — the reset happens
            // on access, which keeps the boundary-burst flaw front and centre.
            if (counter.WindowStart != currentWindowStart)
            {
                counter.WindowStart = currentWindowStart;
                counter.Count = 0;
            }

            var resetsAt = counter.WindowStart + _options.Window;

            if (counter.Count < _options.Limit)
            {
                counter.Count++;
                var remaining = _options.Limit - counter.Count;
                return Task.FromResult(RateLimitResult.Allow(_options.Limit, remaining, resetsAt));
            }

            // Over the limit for this window. Tell the caller exactly how long to wait.
            var retryAfter = resetsAt - now;
            return Task.FromResult(RateLimitResult.Block(_options.Limit, resetsAt, retryAfter));
        }
    }

    // --- Inspection surface (for the debug endpoint / dashboard; NOT part of IRateLimiter) -------

    /// <summary>The configured ceiling per window.</summary>
    public long Limit => _options.Limit;

    /// <summary>The configured window length.</summary>
    public TimeSpan Window => _options.Window;

    /// <summary>
    /// A read-only snapshot of one key's live state. <see cref="StoredCount"/> and
    /// <see cref="StoredWindowStart"/> are the RAW values sitting in the dictionary — note they may
    /// be stale: with lazy reset, a key whose window has elapsed keeps its old count until the next
    /// request touches it. <see cref="WindowActive"/> tells you whether the stored window is the
    /// current one; <see cref="Remaining"/> already accounts for a pending lazy reset.
    /// </summary>
    public record KeyState(
        string Key,
        long StoredCount,
        DateTimeOffset StoredWindowStart,
        bool WindowActive,
        long Remaining,
        DateTimeOffset ResetsAt);

    /// <summary>
    /// Snapshots the internal counter table so you can see exactly what the limiter is holding.
    /// This is what makes the runtime internals observable — you're reading the live dictionary.
    /// </summary>
    public IReadOnlyList<KeyState> GetState()
    {
        var now = _timeProvider.GetUtcNow();
        var currentWindow = AlignToWindow(now);
        var states = new List<KeyState>(_counters.Count);

        foreach (var (key, counter) in _counters)
        {
            long storedCount;
            DateTimeOffset storedWindowStart;
            lock (counter.SyncRoot)
            {
                storedCount = counter.Count;
                storedWindowStart = counter.WindowStart;
            }

            var active = storedWindowStart == currentWindow;
            // If the stored window isn't the current one, a lazy reset is pending — effective count is 0.
            var effectiveCount = active ? storedCount : 0;
            var remaining = Math.Max(0, _options.Limit - effectiveCount);
            var resetsAt = currentWindow + _options.Window;

            states.Add(new KeyState(key, storedCount, storedWindowStart, active, remaining, resetsAt));
        }

        return states;
    }

    /// <summary>
    /// Snaps an instant down to the start of its aligned window. E.g. for a 10s window,
    /// 12:00:07 -> 12:00:00 and 12:00:13 -> 12:00:10. Alignment is relative to ticks=0 (the epoch).
    /// </summary>
    private DateTimeOffset AlignToWindow(DateTimeOffset instant)
    {
        var windowTicks = _options.Window.Ticks;
        var alignedTicks = instant.UtcTicks - (instant.UtcTicks % windowTicks);
        return new DateTimeOffset(alignedTicks, TimeSpan.Zero);
    }

    /// <summary>Mutable per-key state guarded by <see cref="SyncRoot"/>.</summary>
    private sealed class Counter
    {
        public long Count;

        // MinValue guarantees the first access is treated as a new window and resets cleanly.
        public DateTimeOffset WindowStart = DateTimeOffset.MinValue;

        // A dedicated lock object per key. Keeps each key's critical section independent.
        public object SyncRoot { get; } = new();
    }
}
