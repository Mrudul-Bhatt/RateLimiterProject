namespace Level6.TieredQuota.Domain;

public enum Tier { Free, Pro, Enterprise }

/// <summary>How to treat a request that would exceed the monthly credit quota.</summary>
public enum OveragePolicy
{
    Block,  // hard-stop (Free tier): reject
    Meter   // paid tiers: allow, but record the overage for billing
}

/// <summary>
/// A plan/tier definition — the LIMITS for a tier. Seeded once, effectively read-only config.
/// Per-minute and per-day are request-rate caps; monthly is a cost-based CREDIT budget.
/// </summary>
public sealed class Plan
{
    public Tier Tier { get; set; }
    public int RequestsPerMinute { get; set; }
    public int RequestsPerDay { get; set; }
    public long MonthlyCredits { get; set; }
    public OveragePolicy Overage { get; set; }
}

/// <summary>
/// A user's account: which tier they're on, plus the DURABLE monthly credit ledger. The
/// per-minute and per-day counters live in Redis (hot path); only the billing-relevant monthly
/// balance is kept here in Postgres, the source of truth.
///
/// <see cref="MonthAnchor"/> is the UTC start of the current billing period; the reset logic rolls
/// it forward and zeroes usage when a new month begins (idempotently).
/// </summary>
public sealed class Account
{
    public string UserId { get; set; } = default!;
    public Tier Tier { get; set; }
    public long MonthCreditsUsed { get; set; }
    public DateTimeOffset MonthAnchor { get; set; }

    // Optimistic-concurrency token (xmin) — configured in the DbContext.
    public uint Version { get; set; }
}

/// <summary>An immutable audit record of an administrative quota change.</summary>
public sealed class QuotaAuditEntry
{
    public long Id { get; set; }
    public string UserId { get; set; } = default!;
    public string Action { get; set; } = default!;   // e.g. "SetTier", "ResetMonth", "GrantCredits"
    public string Detail { get; set; } = default!;    // human-readable old -> new
    public string By { get; set; } = default!;        // which admin
    public DateTimeOffset At { get; set; }
}
