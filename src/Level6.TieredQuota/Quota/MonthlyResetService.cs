using Level6.TieredQuota.Domain;
using Microsoft.EntityFrameworkCore;

namespace Level6.TieredQuota.Quota;

/// <summary>
/// Proactively resets monthly credit usage for accounts whose billing period has rolled over, so an
/// account that goes idle across a month boundary still gets a fresh balance without waiting for its
/// next request (the enforcement service also resets lazily on access — belt and braces).
///
/// The reset is IDEMPOTENT: it only touches accounts whose <c>MonthAnchor</c> predates the current
/// month, and sets them to exactly that month's start. Running it twice, or alongside the lazy reset,
/// changes nothing the second time. Time comes from the injected TimeProvider, so a test can advance
/// the clock across a month boundary and fire a sweep deterministically.
/// </summary>
public sealed class MonthlyResetService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _clock;
    private readonly ILogger<MonthlyResetService> _logger;
    private readonly TimeSpan _interval;

    public MonthlyResetService(
        IServiceScopeFactory scopeFactory, TimeProvider clock, ILogger<MonthlyResetService> logger, TimeSpan? interval = null)
    {
        _scopeFactory = scopeFactory;
        _clock = clock;
        _logger = logger;
        _interval = interval ?? TimeSpan.FromHours(1);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval, _clock);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Monthly quota reset sweep failed.");
            }
        }
    }

    /// <summary>One idempotent reset pass. Returns how many accounts were rolled over.</summary>
    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var currentMonth = QuotaTime.StartOfMonth(_clock.GetUtcNow());
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuotaDbContext>();

        // Single set-based UPDATE — atomic and idempotent (only rows behind the current month match).
        var reset = await db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE accounts SET ""MonthCreditsUsed"" = 0, ""MonthAnchor"" = {currentMonth}
               WHERE ""MonthAnchor"" < {currentMonth}", ct);

        if (reset > 0)
            _logger.LogInformation("Monthly reset rolled over {Count} account(s) into {Month:yyyy-MM}", reset, currentMonth);
        return reset;
    }
}
