using Level3.Buckets;
using Microsoft.Extensions.Time.Testing;

namespace Level3.Tests;

public class TokenBucketRateLimiterTests
{
    private static (TokenBucketRateLimiter limiter, FakeTimeProvider clock) Create(double capacity, double refillPerSecond)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T12:00:00Z"));
        var limiter = new TokenBucketRateLimiter(new TokenBucketOptions(capacity, refillPerSecond), clock);
        return (limiter, clock);
    }

    [Fact]
    public async Task TokenBucket_allows_burst_up_to_capacity()
    {
        // Bucket starts full, so an idle client can immediately fire a full burst.
        var (limiter, _) = Create(capacity: 10, refillPerSecond: 1);

        for (var i = 1; i <= 10; i++)
            Assert.True((await limiter.CheckAsync("client")).Allowed, $"burst request #{i} should be allowed");

        // 11th has no token left (no time has passed, so nothing refilled).
        Assert.False((await limiter.CheckAsync("client")).Allowed);
    }

    [Fact]
    public async Task TokenBucket_refills_over_time()
    {
        var (limiter, clock) = Create(capacity: 10, refillPerSecond: 2); // 2 tokens/sec

        // Drain the bucket completely.
        for (var i = 0; i < 10; i++) await limiter.CheckAsync("client");
        Assert.False((await limiter.CheckAsync("client")).Allowed);

        // After 2.5s at 2/s, ~5 tokens have accrued.
        clock.Advance(TimeSpan.FromSeconds(2.5));

        for (var i = 1; i <= 5; i++)
            Assert.True((await limiter.CheckAsync("client")).Allowed, $"refilled token #{i} should be allowed");
        Assert.False((await limiter.CheckAsync("client")).Allowed); // only 5 refilled
    }

    [Fact]
    public async Task TokenBucket_caps_accrual_at_capacity()
    {
        var (limiter, clock) = Create(capacity: 5, refillPerSecond: 10);

        // Idle far longer than needed to overfill — accrual must clamp at capacity, not overflow.
        clock.Advance(TimeSpan.FromMinutes(10));

        for (var i = 1; i <= 5; i++)
            Assert.True((await limiter.CheckAsync("client")).Allowed, $"request #{i}");
        Assert.False((await limiter.CheckAsync("client")).Allowed); // capped at 5, not 5 + accrued
    }

    [Fact]
    public async Task TokenBucket_limits_long_run_average_to_refill_rate()
    {
        var (limiter, clock) = Create(capacity: 5, refillPerSecond: 10); // 10/sec sustained
        // Drain the initial burst so we measure steady state, not the burst allowance.
        for (var i = 0; i < 5; i++) await limiter.CheckAsync("client");

        var allowed = 0;
        // Drive for 1 simulated second, 1ms steps: expect ~refill-rate admissions.
        for (var ms = 0; ms < 1000; ms++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            if ((await limiter.CheckAsync("client")).Allowed) allowed++;
        }

        Assert.InRange(allowed, 9, 11); // ~10 admitted over the second
    }

    [Fact]
    public async Task Concurrent_requests_never_exceed_capacity_within_one_process()
    {
        var (limiter, _) = Create(capacity: 5, refillPerSecond: 1);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 200).Select(_ => limiter.CheckAsync("stampede")));

        Assert.Equal(5, results.Count(r => r.Allowed));
    }
}
