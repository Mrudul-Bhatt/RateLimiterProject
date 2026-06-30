using Level2.SlidingWindowLog;
using Microsoft.Extensions.Time.Testing;

namespace Level2.Tests;

/// <summary>
/// Unit tests for the sliding-window-log limiter. Time is driven by FakeTimeProvider so the
/// window can be slid forward instantly and deterministically.
/// </summary>
public class SlidingWindowLogRateLimiterTests
{
    private const long Limit = 5;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private static (SlidingWindowLogRateLimiter limiter, FakeTimeProvider clock) CreateLimiter()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T12:00:00Z"));
        var limiter = new SlidingWindowLogRateLimiter(new SlidingWindowLogOptions(Limit, Window), clock);
        return (limiter, clock);
    }

    [Fact]
    public async Task Allows_up_to_limit_within_window()
    {
        var (limiter, _) = CreateLimiter();

        for (var i = 1; i <= Limit; i++)
        {
            var result = await limiter.CheckAsync("client-a");
            Assert.True(result.Allowed, $"request #{i} should be allowed");
            Assert.Equal(Limit - i, result.Remaining);
        }
    }

    [Fact]
    public async Task Blocks_when_limit_exceeded()
    {
        var (limiter, _) = CreateLimiter();

        for (var i = 0; i < Limit; i++)
            await limiter.CheckAsync("client-a");

        var blocked = await limiter.CheckAsync("client-a");

        Assert.False(blocked.Allowed);
        Assert.Equal(0, blocked.Remaining);
        Assert.NotNull(blocked.RetryAfter);
        Assert.True(blocked.RetryAfter > TimeSpan.Zero);
    }

    /// <summary>
    /// THE PAYOFF OF LEVEL 2.
    ///
    /// This is byte-for-byte the same scenario that PASSED (i.e. let 2x the limit through) in
    /// Level 1's `Demonstrates_boundary_burst`. Here it must BLOCK the second batch, because the
    /// first batch's timestamps are still inside the trailing window when the second batch arrives.
    /// The window slides; there is no edge to exploit.
    /// </summary>
    [Fact]
    public async Task Eliminates_boundary_burst()
    {
        var (limiter, clock) = CreateLimiter();
        const string client = "burster";

        // t = +9s: fill the limit.
        clock.Advance(TimeSpan.FromSeconds(9));
        for (var i = 0; i < Limit; i++)
            Assert.True((await limiter.CheckAsync(client)).Allowed, "first batch fills the window");

        // t = +11s: in Level 1 these slipped through (new fixed window). Here every one is blocked,
        // because the :09 timestamps are still within the last 10 seconds.
        clock.Advance(TimeSpan.FromSeconds(2));
        for (var i = 0; i < Limit; i++)
            Assert.False((await limiter.CheckAsync(client)).Allowed, "second batch is correctly blocked");
    }

    [Fact]
    public async Task Resets_as_oldest_entries_slide_out()
    {
        var (limiter, clock) = CreateLimiter();

        for (var i = 0; i < Limit; i++)
            await limiter.CheckAsync("client-a");
        Assert.False((await limiter.CheckAsync("client-a")).Allowed);

        // Advance a full window so every recorded timestamp slides out.
        clock.Advance(Window);

        var afterReset = await limiter.CheckAsync("client-a");
        Assert.True(afterReset.Allowed);
        Assert.Equal(Limit - 1, afterReset.Remaining);
    }

    [Fact]
    public async Task Evicts_stale_timestamps()
    {
        var (limiter, clock) = CreateLimiter();

        // Two requests, then jump past the window so they become stale.
        await limiter.CheckAsync("client-a");
        await limiter.CheckAsync("client-a");
        clock.Advance(Window + TimeSpan.FromSeconds(1));

        // The next request must see a window containing only itself (the two stale ones evicted).
        var result = await limiter.CheckAsync("client-a");
        Assert.True(result.Allowed);
        Assert.Equal(Limit - 1, result.Remaining);

        var state = limiter.GetState().Single(s => s.Key == "client-a");
        Assert.Equal(1, state.CountInWindow);
    }

    [Fact]
    public async Task Keys_are_isolated_from_each_other()
    {
        var (limiter, _) = CreateLimiter();

        for (var i = 0; i < Limit; i++)
            await limiter.CheckAsync("client-a");
        Assert.False((await limiter.CheckAsync("client-a")).Allowed);

        var b = await limiter.CheckAsync("client-b");
        Assert.True(b.Allowed);
        Assert.Equal(Limit - 1, b.Remaining);
    }

    [Fact]
    public async Task RemoveExpiredKeys_drops_fully_expired_idle_keys()
    {
        var (limiter, clock) = CreateLimiter();

        await limiter.CheckAsync("idle-client");
        Assert.Equal(1, limiter.TrackedKeyCount);

        // Still inside the window: nothing to remove.
        Assert.Equal(0, limiter.RemoveExpiredKeys());
        Assert.Equal(1, limiter.TrackedKeyCount);

        // After the window elapses the log is empty, so the key can be reclaimed.
        clock.Advance(Window + TimeSpan.FromSeconds(1));
        Assert.Equal(1, limiter.RemoveExpiredKeys());
        Assert.Equal(0, limiter.TrackedKeyCount);
    }

    [Fact]
    public async Task Concurrent_requests_never_exceed_limit_within_one_process()
    {
        var (limiter, _) = CreateLimiter();
        const int attempts = 200;

        var results = await Task.WhenAll(
            Enumerable.Range(0, attempts).Select(_ => limiter.CheckAsync("stampede")));

        Assert.Equal(Limit, results.Count(r => r.Allowed));
    }
}
