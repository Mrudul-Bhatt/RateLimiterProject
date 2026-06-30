using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RateLimiting.Abstractions;

namespace Level2.SlidingWindowLog;

/// <summary>
/// Periodically asks the limiter to drop fully-expired, idle keys so the dictionary doesn't grow
/// without bound from one-shot clients (e.g. a flood of unique IPs during an attack).
///
/// This background sweep is a real operational cost that Level 1's O(1)-per-key counter avoided,
/// and that Redis (Level 4) removes again by attaching a TTL to each key. Calling it out is the
/// honest framing: the sliding LOG buys accuracy with both memory and a maintenance loop.
///
/// Uses a PeriodicTimer driven by the injected TimeProvider so the interval is testable.
/// </summary>
public sealed class SlidingWindowLogCleanupService : BackgroundService
{
    private readonly IRateLimiter _limiter;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SlidingWindowLogCleanupService> _logger;
    private readonly TimeSpan _interval;

    public SlidingWindowLogCleanupService(
        IRateLimiter limiter,
        TimeProvider timeProvider,
        ILogger<SlidingWindowLogCleanupService> logger,
        TimeSpan? interval = null)
    {
        _limiter = limiter;
        _timeProvider = timeProvider;
        _logger = logger;
        _interval = interval ?? TimeSpan.FromSeconds(30);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // PeriodicTimer(TimeSpan, TimeProvider) lets tests advance a FakeTimeProvider to fire ticks.
        using var timer = new PeriodicTimer(_interval, _timeProvider);

        while (!stoppingToken.IsCancellationRequested &&
               await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                if (_limiter is SlidingWindowLogRateLimiter slidingLog)
                {
                    var removed = slidingLog.RemoveExpiredKeys();
                    if (removed > 0)
                        _logger.LogInformation("Cleanup removed {Removed} idle rate-limit key(s).", removed);
                }
            }
            catch (Exception ex)
            {
                // A cleanup failure must never take the app down — log and keep sweeping.
                _logger.LogError(ex, "Rate-limit cleanup sweep failed.");
            }
        }
    }
}
