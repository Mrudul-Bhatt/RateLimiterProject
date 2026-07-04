using Level4.RedisAtomic;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Level4.Tests;

[Collection("redis")]
public class RedisSlidingWindowRateLimiterTests
{
    private readonly RedisFixture _fixture;
    public RedisSlidingWindowRateLimiterTests(RedisFixture fixture) => _fixture = fixture;

    private RedisSlidingWindowRateLimiter Create(long limit, TimeSpan window, TimeProvider clock) =>
        new(_fixture.Redis, new RedisSlidingWindowOptions(limit, window, $"t:{Guid.NewGuid():N}:"), clock);

    private static string UniqueKey() => $"k-{Guid.NewGuid():N}";

    [Fact]
    public async Task Redis_sliding_window_allows_and_blocks()
    {
        var limiter = Create(5, TimeSpan.FromSeconds(10), TimeProvider.System);
        var key = UniqueKey();

        for (var i = 1; i <= 5; i++)
            Assert.True((await limiter.CheckAsync(key)).Allowed, $"request #{i}");
        Assert.False((await limiter.CheckAsync(key)).Allowed);
    }

    [Fact]
    public async Task Redis_sliding_window_eliminates_boundary_burst()
    {
        // Same scenario as Level 2, now distributed. FakeTimeProvider drives `now` into the Lua
        // script, so this is deterministic even against a real Redis.
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T12:00:00Z"));
        var limiter = Create(5, TimeSpan.FromSeconds(10), clock);
        var key = UniqueKey();

        clock.Advance(TimeSpan.FromSeconds(9));
        for (var i = 0; i < 5; i++)
            Assert.True((await limiter.CheckAsync(key)).Allowed, "first batch fills the window");

        clock.Advance(TimeSpan.FromSeconds(2)); // t=+11s
        for (var i = 0; i < 5; i++)
            Assert.False((await limiter.CheckAsync(key)).Allowed, "burst across the edge is blocked");
    }

    [Fact]
    public async Task Redis_sliding_window_is_atomic_under_concurrency()
    {
        const long limit = 50;
        var limiter = Create(limit, TimeSpan.FromSeconds(30), TimeProvider.System);
        var key = UniqueKey();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 500).Select(_ => limiter.CheckAsync(key)));

        Assert.Equal(limit, results.Count(r => r.Allowed));
    }
}
