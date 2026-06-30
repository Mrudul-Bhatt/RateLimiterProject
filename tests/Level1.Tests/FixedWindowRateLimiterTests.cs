using Level1.FixedWindow;
using Microsoft.Extensions.Time.Testing;
using RateLimiting.Abstractions;

namespace Level1.Tests;

/// <summary>
/// Unit tests for the fixed-window limiter. Time is driven by FakeTimeProvider, so every
/// "wait for the window to elapse" is instant and deterministic — no Thread.Sleep, no flake.
/// </summary>
public class FixedWindowRateLimiterTests
{
    private const long Limit = 5;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private static (FixedWindowRateLimiter limiter, FakeTimeProvider clock) CreateLimiter()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T12:00:00Z"));
        var limiter = new FixedWindowRateLimiter(new FixedWindowOptions(Limit, Window), clock);
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

    [Fact]
    public async Task Resets_after_window_elapses()
    {
        var (limiter, clock) = CreateLimiter();

        // Exhaust the window.
        for (var i = 0; i < Limit; i++)
            await limiter.CheckAsync("client-a");
        Assert.False((await limiter.CheckAsync("client-a")).Allowed);

        // Advance past the window — the next request starts a fresh window.
        clock.Advance(Window);

        var afterReset = await limiter.CheckAsync("client-a");
        Assert.True(afterReset.Allowed);
        Assert.Equal(Limit - 1, afterReset.Remaining);
    }

    [Fact]
    public async Task Keys_are_isolated_from_each_other()
    {
        var (limiter, _) = CreateLimiter();

        // Exhaust client-a entirely.
        for (var i = 0; i < Limit; i++)
            await limiter.CheckAsync("client-a");
        Assert.False((await limiter.CheckAsync("client-a")).Allowed);

        // client-b is untouched and gets its own full allowance.
        var b = await limiter.CheckAsync("client-b");
        Assert.True(b.Allowed);
        Assert.Equal(Limit - 1, b.Remaining);
    }

    /// <summary>
    /// THE FLAW, captured as a passing test.
    ///
    /// A client sends `Limit` requests at the very end of one window, then `Limit` more at the
    /// very start of the next. Each batch is individually legal, but together they are 2x the
    /// limit inside a ~2-second span straddling the boundary. This test asserts that ALL 2*Limit
    /// requests succeed — i.e. it documents the bug. Level 2 (sliding window) makes the identical
    /// scenario correctly block the overage.
    /// </summary>
    [Fact]
    public async Task Demonstrates_boundary_burst()
    {
        var (limiter, clock) = CreateLimiter();
        const string client = "burster";

        // t = +9s: one second before the 10s window would reset. Fire a full limit's worth.
        clock.Advance(TimeSpan.FromSeconds(9));
        for (var i = 0; i < Limit; i++)
            Assert.True((await limiter.CheckAsync(client)).Allowed, "first batch should fit in window 1");

        // t = +11s: two seconds later, but now in a brand-new window. Fire another full limit.
        clock.Advance(TimeSpan.FromSeconds(2));
        for (var i = 0; i < Limit; i++)
            Assert.True((await limiter.CheckAsync(client)).Allowed, "second batch slips through in window 2");

        // 2 * Limit requests admitted across a 2-second span. That is the boundary burst:
        // the limiter's promise of "Limit per 10s" was violated in practice. This is WHY
        // fixed-window is unsafe for strict limits, and the reason we build sliding window next.
    }

    [Fact]
    public async Task Concurrent_requests_never_exceed_limit_within_one_process()
    {
        var (limiter, _) = CreateLimiter();
        const string client = "stampede";
        const int attempts = 200;

        var results = await Task.WhenAll(
            Enumerable.Range(0, attempts).Select(_ => limiter.CheckAsync(client)));

        var allowed = results.Count(r => r.Allowed);

        // The per-key lock guarantees correctness under concurrency *inside this process*.
        // (Across multiple processes this guarantee does not hold — that is Level 4's problem.)
        Assert.Equal(Limit, allowed);
    }
}
