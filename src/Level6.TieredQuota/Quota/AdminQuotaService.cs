using Level6.TieredQuota.Domain;
using Microsoft.EntityFrameworkCore;

namespace Level6.TieredQuota.Quota;

public sealed record QuotaAdjustment(Tier? SetTier, bool ResetMonth, long? GrantCredits);

/// <summary>
/// The customer-success / admin path: manually change a user's tier, reset their monthly usage, or
/// grant bonus credits. Every change writes an immutable <see cref="QuotaAuditEntry"/> — who did
/// what, when, and the before→after — because manual quota edits are exactly the kind of thing you
/// must be able to answer for later.
/// </summary>
public sealed class AdminQuotaService
{
    private readonly QuotaDbContext _db;
    private readonly TimeProvider _clock;

    public AdminQuotaService(QuotaDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<Account?> AdjustAsync(string userId, QuotaAdjustment change, string adminId, CancellationToken ct = default)
    {
        var account = await _db.Accounts.FirstOrDefaultAsync(a => a.UserId == userId, ct);
        if (account is null) return null;

        var now = _clock.GetUtcNow();

        if (change.SetTier is { } tier && tier != account.Tier)
        {
            Audit(userId, "SetTier", $"{account.Tier} -> {tier}", adminId, now);
            account.Tier = tier;
        }

        if (change.ResetMonth)
        {
            Audit(userId, "ResetMonth", $"credits {account.MonthCreditsUsed} -> 0", adminId, now);
            account.MonthCreditsUsed = 0;
            account.MonthAnchor = QuotaTime.StartOfMonth(now);
        }

        if (change.GrantCredits is { } grant && grant != 0)
        {
            // A grant reduces recorded usage (never below zero) — it hands the user more headroom.
            var newUsed = Math.Max(0, account.MonthCreditsUsed - grant);
            Audit(userId, "GrantCredits", $"used {account.MonthCreditsUsed} -> {newUsed} (grant {grant})", adminId, now);
            account.MonthCreditsUsed = newUsed;
        }

        await _db.SaveChangesAsync(ct);
        return account;
    }

    private void Audit(string userId, string action, string detail, string by, DateTimeOffset at) =>
        _db.AuditEntries.Add(new QuotaAuditEntry { UserId = userId, Action = action, Detail = detail, By = by, At = at });
}
