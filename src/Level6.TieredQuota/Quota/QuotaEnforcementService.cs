using Level4.RedisAtomic;
using Level6.TieredQuota.Domain;
using Microsoft.EntityFrameworkCore;

namespace Level6.TieredQuota.Quota;

public enum QuotaWindow { None, Minute, Day, Month }

/// <summary>Optional prefix for all Redis quota keys (isolates tests / tenants).</summary>
public sealed record RedisKeyNamespace(string Value);

/// <summary>The outcome of enforcing all quota windows for one request.</summary>
public sealed record QuotaDecision(
    bool Allowed,
    QuotaWindow LimitHit,
    Tier Tier,
    long Cost,
    long MonthUsed,
    long MonthLimit,
    long MonthOverage,
    TimeSpan RetryAfter)
{
    public long MonthRemaining => Math.Max(0, MonthLimit - MonthUsed);
}

/// <summary>
/// Enforces a tier's THREE concurrent quota windows for a request, in cheapest/shortest-first order,
/// short-circuiting on the first one that fails:
///
///   1. per-minute  (Redis, cost-weighted units)  — a hot burst cap
///   2. per-day     (Redis, request count)         — a coarse daily volume cap
///   3. per-month   (Postgres, cost-based credits) — the durable, billing-relevant budget
///
/// Hot vs durable split: the high-frequency minute/day counters live in Redis with TTLs; only the
/// billing-relevant monthly ledger is kept durably in Postgres (the source of truth).
///
/// Fail-fast caveat (same as Level 5): a request rejected by a later window has already debited the
/// earlier ones. Shortest-first ordering keeps that waste small. The monthly credit debit is last,
/// so credits are only ever charged when the request actually passes minute + day.
/// </summary>
public sealed class QuotaEnforcementService
{
    private readonly QuotaDbContext _db;
    private readonly RedisCostWindow _redis;
    private readonly TimeProvider _clock;
    private readonly ILogger<QuotaEnforcementService> _logger;
    private readonly string _ns;

    public QuotaEnforcementService(
        QuotaDbContext db, RedisCostWindow redis, TimeProvider clock,
        ILogger<QuotaEnforcementService> logger, RedisKeyNamespace ns)
    {
        _db = db;
        _redis = redis;
        _clock = clock;
        _logger = logger;
        _ns = ns.Value;
    }

    public async Task<QuotaDecision> CheckAndDebitAsync(string userId, long cost, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var account = await GetOrCreateAccountAsync(userId, now, ct);
        await ResetMonthIfElapsed(account, now, ct);
        var plan = await _db.Plans.AsNoTracking().FirstAsync(p => p.Tier == account.Tier, ct);

        // 1. Per-minute (Redis) — a request-RATE cap (one request = one unit). We still route it
        // through the cost-based Lua (which can debit any amount); the variable cost is applied to the
        // monthly CREDIT budget below, not to the rate windows.
        var minuteKey = $"{_ns}q:min:{userId}:{QuotaTime.MinuteBucket(now)}";
        var minute = await _redis.TryDebitAsync(minuteKey, plan.RequestsPerMinute, 1, TimeSpan.FromMinutes(2));
        if (!minute.Allowed)
            return Reject(QuotaWindow.Minute, account.Tier, cost, account, plan, QuotaTime.NextMinute(now) - now);

        // 2. Per-day (Redis) — one request = one unit; the date-keyed bucket resets at midnight UTC.
        var dayKey = $"{_ns}q:day:{userId}:{QuotaTime.DayBucket(now)}";
        var day = await _redis.TryDebitAsync(dayKey, plan.RequestsPerDay, 1, TimeSpan.FromHours(26));
        if (!day.Allowed)
            return Reject(QuotaWindow.Day, account.Tier, cost, account, plan, QuotaTime.NextMidnight(now) - now);

        // 3. Per-month credits (Postgres) — durable, cost-based, overage-aware.
        var month = await DebitMonthlyCreditsAsync(userId, cost, plan, ct);
        if (!month.allowed)
            return Reject(QuotaWindow.Month, account.Tier, cost, account, plan, QuotaTime.StartOfNextMonth(now) - now, month.used);

        var overage = Math.Max(0, month.used - plan.MonthlyCredits);
        _logger.LogInformation(
            "QUOTA ALLOW {User} tier={Tier} cost={Cost} monthUsed={Used}/{Limit} overage={Overage}",
            userId, account.Tier, cost, month.used, plan.MonthlyCredits, overage);
        return new QuotaDecision(true, QuotaWindow.None, account.Tier, cost, month.used, plan.MonthlyCredits, overage, TimeSpan.Zero);
    }

    private QuotaDecision Reject(QuotaWindow window, Tier tier, long cost, Account acct, Plan plan, TimeSpan retryAfter, long? monthUsed = null)
    {
        _logger.LogWarning("QUOTA BLOCK {User} tier={Tier} window={Window} cost={Cost}", acct.UserId, tier, window, cost);
        return new QuotaDecision(false, window, tier, cost, monthUsed ?? acct.MonthCreditsUsed, plan.MonthlyCredits,
            Math.Max(0, (monthUsed ?? acct.MonthCreditsUsed) - plan.MonthlyCredits), retryAfter);
    }

    /// <summary>
    /// Atomic monthly credit debit in Postgres. For Block plans it's a conditional UPDATE (0 rows =
    /// over quota, rejected). For Meter plans it's unconditional (always allowed, overage recorded as
    /// used-beyond-limit). Either way it's a single atomic statement — correct under concurrency.
    /// </summary>
    private async Task<(bool allowed, long used)> DebitMonthlyCreditsAsync(string userId, long cost, Plan plan, CancellationToken ct)
    {
        int rows;
        if (plan.Overage == OveragePolicy.Block)
        {
            rows = await _db.Database.ExecuteSqlInterpolatedAsync(
                $@"UPDATE accounts SET ""MonthCreditsUsed"" = ""MonthCreditsUsed"" + {cost}
                   WHERE ""UserId"" = {userId} AND ""MonthCreditsUsed"" + {cost} <= {plan.MonthlyCredits}", ct);
        }
        else // Meter: allow past the limit, record the overage.
        {
            rows = await _db.Database.ExecuteSqlInterpolatedAsync(
                $@"UPDATE accounts SET ""MonthCreditsUsed"" = ""MonthCreditsUsed"" + {cost}
                   WHERE ""UserId"" = {userId}", ct);
        }

        var used = await _db.Accounts.AsNoTracking().Where(a => a.UserId == userId)
            .Select(a => a.MonthCreditsUsed).FirstAsync(ct);
        return (rows > 0, used);
    }

    private async Task<Account> GetOrCreateAccountAsync(string userId, DateTimeOffset now, CancellationToken ct)
    {
        var account = await _db.Accounts.FirstOrDefaultAsync(a => a.UserId == userId, ct);
        if (account is not null) return account;

        account = new Account { UserId = userId, Tier = Tier.Free, MonthCreditsUsed = 0, MonthAnchor = QuotaTime.StartOfMonth(now) };
        _db.Accounts.Add(account);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) // lost a race to create the same account — reload the winner.
        {
            _db.Entry(account).State = EntityState.Detached;
            account = await _db.Accounts.FirstAsync(a => a.UserId == userId, ct);
        }
        return account;
    }

    /// <summary>Lazy, idempotent monthly reset: if the stored anchor predates the current month, zero usage and roll forward.</summary>
    private async Task ResetMonthIfElapsed(Account account, DateTimeOffset now, CancellationToken ct)
    {
        var currentMonth = QuotaTime.StartOfMonth(now);
        if (account.MonthAnchor >= currentMonth) return;

        account.MonthCreditsUsed = 0;
        account.MonthAnchor = currentMonth;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("QUOTA RESET {User} monthly credits reset for {Month:yyyy-MM}", account.UserId, currentMonth);
    }
}
