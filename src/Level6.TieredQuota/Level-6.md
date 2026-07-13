# Level 6 — Tiered Limits & Quota Management (study notes)

This is the "senior answer" level. Stripe / GitHub / OpenAI don't enforce one limit — they enforce
**multiple concurrent windows** (per-minute *and* per-day *and* per-month) with **plan-aware** quotas
and **cost-based** accounting, split across a hot store and a durable one.

Stack: Redis (hot windows) + PostgreSQL/EF Core (durable ledger + plans + audit).

---

## 1. The three concurrent windows

Every request is checked against three windows at once, **shortest-first, short-circuit on the first
failure** ([QuotaEnforcementService.cs](./Quota/QuotaEnforcementService.cs)):

| Window | Store | Meaning | Resets |
|---|---|---|---|
| per-minute | Redis | request-rate burst cap | TTL / minute-keyed |
| per-day | Redis | coarse daily volume cap | date-keyed → midnight UTC |
| per-month | **Postgres** | cost-based CREDIT budget (billing) | reset job + lazy, at month anchor |

### Hot vs durable split (the key design call)
The high-frequency minute/day counters are disposable and live in **Redis** (fast, TTL'd). The
**billing-relevant** monthly credit balance is the source of truth, so it lives durably in
**Postgres**. Losing a minute counter on a Redis restart is fine; losing a customer's month-to-date
credit usage is not. Interviewers love this question — state the reasoning, not just the split.

---

## 2. Cost-based accounting

Not every request costs the same. `/api/basic` debits **1** credit; `/api/image` debits **10**. The
per-minute/day windows still count *requests* (one unit each — a rate cap), but the monthly ledger
debits the real **cost**.

- **Redis** cost debit: `cost_window.lua` uses `INCRBY cost` (not `INCR`) and rejects if the cost
  wouldn't fit — atomic, exactly as the roadmap asks ("extend the Lua to accept a cost argument").
- **Postgres** cost debit: a single atomic `UPDATE ... SET month_credits_used = month_credits_used +
  cost WHERE ... <= limit` — the row-level write is the concurrency guard.

---

## 3. Plans & tiers

Seeded into Postgres, one row per tier (limits are config-overridable so tests can use tiny values):

| Tier | rpm | rpd | credits/mo | overage |
|---|---|---|---|---|
| Free | 5 | 100 | 1,000 | **Block** |
| Pro | 60 | 10,000 | 100,000 | **Meter** |
| Enterprise | 600 | 500,000 | 5,000,000 | Meter |

An account references its tier; new callers auto-provision as Free.

---

## 4. Overage: block vs meter

The monthly credit check honours the plan's **overage policy**:
- **Block** (Free): a request that would exceed the budget is rejected (`429`, `window=Month`).
- **Meter** (paid): the request is *allowed* past the limit and the excess is recorded as
  `overage` — you bill for it later. Implemented as a conditional vs unconditional `UPDATE`.

---

## 5. Resets — idempotent, injectable clock

- **Daily** resets for free: the Redis day counter is **date-keyed** (`q:day:{user}:{yyyyMMdd}`), so
  crossing midnight UTC yields a new key and a fresh count. The date comes from an injected
  `TimeProvider`, so `Daily_quota_resets_at_midnight_utc` advances a `FakeTimeProvider` across
  midnight — no real waiting.
- **Monthly** resets need a real job (a billing anchor isn't a TTL):
  [MonthlyResetService.cs](./Quota/MonthlyResetService.cs) sweeps accounts whose `MonthAnchor`
  predates the current month and zeroes them. The enforcement service *also* resets lazily on access.
  Both are **idempotent** (they only touch rows behind the current month), so running the job twice,
  or alongside the lazy path, is harmless.

**Never `DateTime.Now`.** Every time decision flows through `TimeProvider` → deterministic tests.

---

## 6. Admin API + audit trail

Customer-success needs to adjust quotas by hand — and every such change must be answerable for later.
[AdminQuotaService.cs](./Quota/AdminQuotaService.cs) supports set-tier / reset-month / grant-credits,
and writes an immutable `QuotaAuditEntry` (who, what, before→after, when) for each. `POST
/admin/quota/{user}` (guarded by `X-Admin-Token`); `GET /admin/audit/{user}` reads the trail.

---

## 7. Fail-fast caveat (carried from Level 5)

Windows are checked minute → day → month and short-circuit. A request rejected by a later window has
already debited the earlier ones. Shortest-first ordering keeps the waste small, and the monthly
credit debit is **last**, so credits are only charged when the request has already cleared the rate
windows — you never bill for a rate-limited call.

---

## 8. Tests (Testcontainers Postgres + Redis + WebApplicationFactory)

Each test gets its own Postgres **database** (isolated seed/plans) and a unique Redis **key prefix**
(the Redis container is shared), plus a `FakeTimeProvider` injected via `ConfigureServices`.

| Test | Asserts |
|---|---|
| `Free_tier_blocked_at_lower_limit_than_pro` | Free (rpm 5) blocks where Pro (rpm 60) allows. |
| `Per_minute_limit_trips_before_daily` | 6th request → 429 `window=Minute` (minute is tighter/first). |
| `Image_request_debits_10_credits` | One image call → `monthUsed == 10`. |
| `Daily_quota_resets_at_midnight_utc` | Day cap hit, then a fresh allowance after crossing midnight. |
| `Paid_tier_allows_metered_overage` | Pro (Meter) goes past budget with `overage>0`; Free (Block) 429s. |
| `Admin_can_adjust_user_quota_and_it_is_audited` | Tier change takes effect and appears in the audit trail. |

---

## 9. Run it

```bash
docker compose up -d                                             # Redis + Postgres
dotnet test tests/Level6.Tests                                   # 6 tests (own containers)
dotnet run --project src/Level6.TieredQuota --urls http://localhost:5096
```

Open **http://localhost:5096/** — pick a user, fire basic/image calls (watch the monthly credit bar),
and use the admin panel to change tiers / reset / grant (audited). Config: `Plans:{Tier}:{Rpm,Rpd,
Credits,Overage}`, `Admin:Token`, `Redis:*`, `Postgres:ConnectionString`.

---

## Interview takeaway (say it cleanly)

> Tiered quotas mean multiple concurrent windows enforced together, shortest-first: per-minute and
> per-day rate caps in Redis (cheap, disposable, TTL'd) and a durable, cost-based monthly credit
> ledger in Postgres (the billing source of truth). Not every request costs 1 — the Lua does
> `INCRBY cost` and Postgres debits the same cost atomically. Overage is a per-plan policy: hard block
> for free, metered (allow + record) for paid. Resets run off an injected clock and are idempotent, so
> the job and the lazy-on-access path can both run safely. And every manual admin change is audited.
