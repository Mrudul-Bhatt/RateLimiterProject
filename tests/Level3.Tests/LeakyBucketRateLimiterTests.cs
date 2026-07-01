using Level3.Buckets;
using Microsoft.Extensions.Time.Testing;

namespace Level3.Tests;

public class LeakyBucketRateLimiterTests
{
    private static (LeakyBucketRateLimiter limiter, FakeTimeProvider clock) Create(int capacity, double leakPerSecond)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T12:00:00Z"));
        var limiter = new LeakyBucketRateLimiter(new LeakyBucketOptions(capacity, leakPerSecond), clock);
        return (limiter, clock);
    }

    [Fact]
    public async Task LeakyBucket_drops_overflow()
    {
        // Capacity 5, drains at 1/sec. A burst of 8 at one instant: 5 fit, 3 overflow and drop.
        var (limiter, _) = Create(capacity: 5, leakPerSecond: 1);

        var allowed = 0;
        for (var i = 0; i < 8; i++)
            if ((await limiter.CheckAsync("client")).Allowed) allowed++;

        Assert.Equal(5, allowed);
    }

    [Fact]
    public async Task LeakyBucket_enforces_constant_output_rate()
    {
        // Capacity 1 = pure smoothing, no burst. Rate 10/sec => one admission every 100ms, exactly.
        var (limiter, clock) = Create(capacity: 1, leakPerSecond: 10);

        Assert.True((await limiter.CheckAsync("client")).Allowed);   // t=0     admit
        Assert.False((await limiter.CheckAsync("client")).Allowed);  // t=0     too soon

        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True((await limiter.CheckAsync("client")).Allowed);   // t=100   admit

        clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.False((await limiter.CheckAsync("client")).Allowed);  // t=150   too soon

        clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.True((await limiter.CheckAsync("client")).Allowed);   // t=200   admit
    }

    [Fact]
    public async Task LeakyBucket_smooths_a_burst_into_evenly_spaced_releases()
    {
        // The heart of "smooth output": even when a whole burst is ACCEPTED at once, the scheduled
        // release times (carried in ResetsAt) are spaced exactly one interval apart.
        var (limiter, _) = Create(capacity: 5, leakPerSecond: 2); // interval = 500ms

        var releases = new List<DateTimeOffset>();
        for (var i = 0; i < 5; i++)
        {
            var r = await limiter.CheckAsync("client");
            Assert.True(r.Allowed);
            releases.Add(r.ResetsAt);
        }

        for (var i = 1; i < releases.Count; i++)
            Assert.Equal(TimeSpan.FromMilliseconds(500), releases[i] - releases[i - 1]);
    }

    [Fact]
    public async Task LeakyBucket_recovers_capacity_as_it_drains()
    {
        var (limiter, clock) = Create(capacity: 3, leakPerSecond: 1); // one drains per second

        for (var i = 0; i < 3; i++) Assert.True((await limiter.CheckAsync("client")).Allowed);
        Assert.False((await limiter.CheckAsync("client")).Allowed); // full

        clock.Advance(TimeSpan.FromSeconds(2)); // two drain out
        Assert.True((await limiter.CheckAsync("client")).Allowed);
        Assert.True((await limiter.CheckAsync("client")).Allowed);
        Assert.False((await limiter.CheckAsync("client")).Allowed); // full again
    }
}
