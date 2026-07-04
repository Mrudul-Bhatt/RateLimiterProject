using Level4.RedisAtomic;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;
using Xunit;

namespace Level4.Tests;

[Collection("redis")]
public class RedisFixedWindowRateLimiterTests
{
    private readonly RedisFixture _fixture;
    public RedisFixedWindowRateLimiterTests(RedisFixture fixture) => _fixture = fixture;

    private RedisFixedWindowRateLimiter Create(long limit, TimeSpan window, string prefix) =>
        new(_fixture.Redis, new RedisFixedWindowOptions(limit, window, prefix), TimeProvider.System);

    private static string UniqueKey() => $"k-{Guid.NewGuid():N}";

    [Fact]
    public async Task Redis_fixed_window_allows_and_blocks()
    {
        var limiter = Create(5, TimeSpan.FromSeconds(30), $"t:{Guid.NewGuid():N}:");
        var key = UniqueKey();

        for (var i = 1; i <= 5; i++)
        {
            var r = await limiter.CheckAsync(key);
            Assert.True(r.Allowed, $"request #{i} should be allowed");
            Assert.Equal(5 - i, r.Remaining);
        }

        var blocked = await limiter.CheckAsync(key);
        Assert.False(blocked.Allowed);
        Assert.Equal(0, blocked.Remaining);
    }

    [Fact]
    public async Task Redis_fixed_window_resets_after_ttl_expires()
    {
        // Short real window so we can wait out the Redis TTL (reset is driven by key expiry).
        var limiter = Create(3, TimeSpan.FromSeconds(1), $"t:{Guid.NewGuid():N}:");
        var key = UniqueKey();

        for (var i = 0; i < 3; i++) Assert.True((await limiter.CheckAsync(key)).Allowed);
        Assert.False((await limiter.CheckAsync(key)).Allowed);

        await Task.Delay(TimeSpan.FromMilliseconds(1200)); // let the key expire in Redis

        Assert.True((await limiter.CheckAsync(key)).Allowed); // fresh window
    }

    [Fact]
    public async Task Redis_fixed_window_is_atomic_under_concurrency()
    {
        // The headline guarantee: hammer the SAME key from many concurrent callers (simulating many
        // app instances hitting shared Redis). The Lua script's atomicity means the number admitted
        // is EXACTLY the limit — never more.
        const long limit = 50;
        var limiter = Create(limit, TimeSpan.FromSeconds(30), $"t:{Guid.NewGuid():N}:");
        var key = UniqueKey();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 500).Select(_ => limiter.CheckAsync(key)));

        Assert.Equal(limit, results.Count(r => r.Allowed));
    }
}
