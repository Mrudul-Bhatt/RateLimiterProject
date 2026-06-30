using Level2.SlidingWindowLog;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Level2.Tests;

public class SlidingWindowLogCleanupServiceTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Cleanup_service_removes_idle_keys()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T12:00:00Z"));
        var limiter = new SlidingWindowLogRateLimiter(new SlidingWindowLogOptions(5, Window), clock);

        // Seed an idle key, then let it age out of its window.
        await limiter.CheckAsync("idle-client");
        Assert.Equal(1, limiter.TrackedKeyCount);
        clock.Advance(Window + TimeSpan.FromSeconds(1));

        var service = new SlidingWindowLogCleanupService(
            limiter, clock, NullLogger<SlidingWindowLogCleanupService>.Instance, SweepInterval);

        await service.StartAsync(CancellationToken.None);
        try
        {
            // Fire one sweep by advancing the fake clock past the interval.
            clock.Advance(SweepInterval);

            // The sweep runs on a background continuation; poll briefly for it to land.
            await WaitUntilAsync(() => limiter.TrackedKeyCount == 0, TimeSpan.FromSeconds(2));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Equal(0, limiter.TrackedKeyCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
    }
}
