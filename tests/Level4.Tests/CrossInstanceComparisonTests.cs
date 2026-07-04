using Level1.FixedWindow;
using Level4.RedisAtomic;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;
using Xunit;

namespace Level4.Tests;

/// <summary>
/// The problem Level 4 exists to solve, demonstrated as a test:
/// two INDEPENDENT in-memory limiters (each simulating a separate app instance with its own memory)
/// admit ~2x the intended limit for the same client, whereas two Redis-backed limiters sharing one
/// Redis hold the global limit exactly.
/// </summary>
[Collection("redis")]
public class CrossInstanceComparisonTests
{
    private readonly RedisFixture _fixture;
    public CrossInstanceComparisonTests(RedisFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task InMemory_limiter_violates_limit_across_instances()
    {
        const long limit = 10;
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T12:00:00Z"));

        // Two separate processes == two separate ConcurrentDictionaries.
        var instanceA = new FixedWindowRateLimiter(new FixedWindowOptions(limit, TimeSpan.FromSeconds(30)), clock);
        var instanceB = new FixedWindowRateLimiter(new FixedWindowOptions(limit, TimeSpan.FromSeconds(30)), clock);

        var allowed = 0;
        // A load balancer sprays the same client across both instances.
        for (var i = 0; i < 30; i++)
        {
            var limiter = i % 2 == 0 ? instanceA : instanceB;
            if ((await limiter.CheckAsync("same-client")).Allowed) allowed++;
        }

        // Each instance independently allowed up to `limit`, so the client got ~2x the global limit.
        Assert.True(allowed > limit, $"expected the limit to be violated; allowed={allowed}");
        Assert.Equal(2 * limit, allowed);
    }

    [Fact]
    public async Task Redis_limiter_holds_limit_across_instances()
    {
        const long limit = 10;
        var prefix = $"t:{Guid.NewGuid():N}:";
        var options = new RedisFixedWindowOptions(limit, TimeSpan.FromSeconds(30), prefix);

        // Two limiter instances, but ONE shared Redis == one shared counter.
        var instanceA = new RedisFixedWindowRateLimiter(_fixture.Redis, options, TimeProvider.System);
        var instanceB = new RedisFixedWindowRateLimiter(_fixture.Redis, options, TimeProvider.System);
        var key = $"same-client-{Guid.NewGuid():N}";

        var allowed = 0;
        for (var i = 0; i < 30; i++)
        {
            var limiter = i % 2 == 0 ? instanceA : instanceB;
            if ((await limiter.CheckAsync(key)).Allowed) allowed++;
        }

        Assert.Equal(limit, allowed); // exactly the global limit, regardless of instance count
    }
}
