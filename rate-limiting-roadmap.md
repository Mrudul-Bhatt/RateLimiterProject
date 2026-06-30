# Rate Limiting — Level-by-Level Build Roadmap (C# / .NET 8)

A progression from "what is rate limiting?" to a production-grade, observable, distributed rate limiter. Each level is a runnable project with tests. Build them in order — every level reuses concepts (and ideally code) from the one before.

**Tech stack:** C# / .NET 8, ASP.NET Core, StackExchange.Redis, xUnit, Docker Compose, Prometheus, Grafana, OpenTelemetry.

**How to use this file with Claude Code:** Open this repo in VSCode, point Claude Code at this file, and say *"Build Level N from rate-limiting-roadmap.md"*. Each level below has an explicit deliverables list, acceptance criteria, and the key insight you should be able to explain in an interview afterward.

> **Note on .NET's built-in limiter:** ASP.NET Core ships `System.Threading.RateLimiting` and `app.UseRateLimiter()` (the `Microsoft.AspNetCore.RateLimiting` middleware). The early levels deliberately build the algorithms by hand so you understand them; later levels compare your implementation against the built-in one and the production levels layer on top of it. Knowing *both* the from-scratch mechanics and the framework primitive is the SDE-2 differentiator.

---

## Suggested repository layout

```
rate-limiting-dotnet/
├── README.md
├── rate-limiting-roadmap.md        # this file
├── docker-compose.yml              # Redis, Prometheus, Grafana (added at L4/L7)
├── src/
│   ├── Level1.FixedWindow/
│   ├── Level2.SlidingWindowLog/
│   ├── Level3.Buckets/
│   ├── Level4.RedisAtomic/
│   ├── Level5.Middleware/
│   ├── Level6.TieredQuota/
│   └── Level7.GatewayObservability/
├── shared/
│   └── RateLimiting.Abstractions/  # IRateLimiter, RateLimitResult, etc.
└── tests/
    ├── Level1.Tests/
    ├── Level2.Tests/
    └── ...
```

A shared `IRateLimiter` abstraction introduced at Level 1 and refined as you go keeps the levels composable:

```csharp
public record RateLimitResult(
    bool Allowed,
    long Limit,
    long Remaining,
    DateTimeOffset ResetsAt,
    TimeSpan? RetryAfter);

public interface IRateLimiter
{
    // key = client identity (IP, user id, api key, or composite)
    Task<RateLimitResult> CheckAsync(string key, CancellationToken ct = default);
}
```

---

# Foundation

## Level 1 — Fixed window counter
**Time:** ~20–40 min · **Stack:** in-memory only, no Redis · **Difficulty:** intro

### Goal
Build a minimal ASP.NET Core app with a `ConcurrentDictionary`-based counter that resets every N seconds per client key. Understand *why the boundary burst problem exists* — this is the whole point of Level 1.

### Deliverables
- ASP.NET Core minimal API with one protected endpoint (e.g. `GET /api/resource`).
- `FixedWindowRateLimiter : IRateLimiter` backed by a `ConcurrentDictionary<string, Counter>`.
- Counter holds `{ long Count, DateTimeOffset WindowStart }`; reset when the current window expires (lazy reset on access — avoid a background timer at this level so the flaw is obvious).
- Returns `429 Too Many Requests` with a `Retry-After` header when the limit is exceeded.
- Emits standard headers on every response: `X-RateLimit-Limit`, `X-RateLimit-Remaining`, `X-RateLimit-Reset`.
- Client key derived from IP (`HttpContext.Connection.RemoteIpAddress`) for now.

### Key insight to demonstrate
A client can send `limit` requests at `11:59:59` and `limit` more at `12:00:01` — **2× the limit across a 2-second span** — and slip through, because each lands in a different window. Write a test that proves this.

### Acceptance criteria / tests (xUnit)
- `Allows_up_to_limit_within_window`
- `Blocks_when_limit_exceeded`
- `Resets_after_window_elapses`
- `Demonstrates_boundary_burst` — fires `limit` requests just before reset and `limit` just after, asserts **all** succeed (the bug, captured as a passing test with a comment explaining why it's bad).

### Interview takeaway
Fixed window is O(1) memory and dead simple, but allows up to 2× burst at window edges. State the flaw precisely.

---

## Level 2 — Sliding window log
**Time:** ~2–3 hrs · **Stack:** in-memory · **Difficulty:** easy

### Goal
Store every request timestamp per key. On each request, evict timestamps older than the window, then count what remains. Precise — but memory grows with traffic, not just with the number of keys.

### Deliverables
- `SlidingWindowLogRateLimiter : IRateLimiter` using a per-key `Queue<long>` (or sorted structure) of millisecond timestamps.
- On each `CheckAsync`: dequeue all entries older than `now - window`, then compare `Count` to the limit.
- A background cleanup `IHostedService` that periodically evicts keys whose logs are fully expired (prevents unbounded growth from one-shot clients).
- A small benchmark (BenchmarkDotNet or a simple stopwatch harness) comparing memory footprint vs Level 1 under sustained load.

### Key insight to demonstrate
- Boundary burst is **eliminated** — the window truly slides.
- But memory is **O(requests in window)** per key, not O(1). Under high traffic this is the reason real systems rarely use a pure log.

### Acceptance criteria / tests
- `Eliminates_boundary_burst` — the same scenario that passed in Level 1 now correctly **blocks** the overage.
- `Evicts_stale_timestamps`
- `Cleanup_service_removes_idle_keys`
- Benchmark output captured in the README showing memory growth vs Level 1.

### Interview takeaway
"What's wrong with a sliding log?" → Accurate but memory scales with request volume. Good segue to sliding-window-counter (a hybrid) and to Redis sorted sets at Level 4.

---

## Level 3 — Token bucket & leaky bucket
**Time:** ~3–4 hrs · **Stack:** in-memory · **Difficulty:** medium

### Goal
Implement both algorithms side by side. Token bucket allows bursts up to a capacity; leaky bucket enforces a strictly smooth output rate. Same traffic in, very different behaviour out.

### Deliverables
- `TokenBucketRateLimiter : IRateLimiter`
  - State per key: `{ double Tokens, DateTimeOffset LastRefill }`.
  - Lazy refill: on each request, add `(now - lastRefill) * refillRatePerSecond` tokens, capped at `capacity`. Consume 1 token per request; reject if `< 1`.
- `LeakyBucketRateLimiter : IRateLimiter`
  - Model a fixed-capacity queue drained at a constant rate. Requests beyond capacity are dropped (or queued, your choice — document it).
- A side-by-side demo endpoint or console harness that feeds an identical **bursty** traffic pattern to both and logs accept/reject per request.
- Load driver: use a small C# load loop, `dotnet-counters`, or an external tool (k6/bombardier) to generate the burst.

### Key insight to demonstrate
- Token bucket **absorbs a burst** (up to capacity) that leaky bucket would reject.
- Leaky bucket produces a **perfectly smooth** output stream regardless of arrival spikes.
- These map to different real goals: token bucket = "allow occasional bursts, limit average"; leaky bucket = "protect a downstream that can only handle X/sec".

### Acceptance criteria / tests
- `TokenBucket_allows_burst_up_to_capacity`
- `TokenBucket_refills_over_time`
- `LeakyBucket_enforces_constant_output_rate`
- `LeakyBucket_drops_overflow`
- A captured comparison log/chart showing the divergent behaviour on identical input.

### Interview takeaway
Be able to draw both and say when you'd pick each. Token bucket is the most common API rate limiter (it's what AWS, Stripe-style limits use); leaky bucket is for traffic shaping toward a fragile downstream.

---

# Distributed Systems

## Level 4 — Redis-backed atomic counter
**Time:** ~4–5 hrs · **Stack:** Redis + StackExchange.Redis + Docker Compose · **Difficulty:** medium-hard

### Goal
Move state out of process memory and into Redis so the limit is correct across N application instances. Use Lua scripts to make the read-modify-write **atomic** — no race conditions, no distributed locks.

### Deliverables
- `docker-compose.yml` with a Redis service.
- `RedisFixedWindowRateLimiter` using `INCR` + `EXPIRE` (set TTL only on first increment).
- `RedisSlidingWindowRateLimiter` using a sorted set: `ZADD` the timestamp, `ZREMRANGEBYSCORE` to drop old entries, `ZCARD`/`ZCOUNT` to count — all inside one Lua script.
- A **race-condition demonstration**: run the in-memory limiter from Level 1 across 2+ concurrent workers/instances and show the limit is violated; then show Redis `INCR` holds the line.
- Benchmark: measure added latency of a Redis round trip vs in-process.

### Why Lua
A naive `GET` → check → `INCR` from the app has a TOCTOU race between instances. A Lua script runs atomically on the Redis server in a single round trip, so increment + expiry + limit check happen as one indivisible operation. Provide the script as an embedded resource and load it with `ScriptEvaluateAsync`.

Example sliding-window Lua sketch (refine in code):
```lua
-- KEYS[1] = rate limit key
-- ARGV[1] = now (ms), ARGV[2] = window (ms), ARGV[3] = limit, ARGV[4] = member (unique)
redis.call('ZREMRANGEBYSCORE', KEYS[1], 0, ARGV[1] - ARGV[2])
local count = redis.call('ZCARD', KEYS[1])
if count < tonumber(ARGV[3]) then
  redis.call('ZADD', KEYS[1], ARGV[1], ARGV[4])
  redis.call('PEXPIRE', KEYS[1], ARGV[2])
  return {1, ARGV[3] - count - 1}   -- allowed, remaining
else
  return {0, 0}                     -- blocked
end
```

### Key insight to demonstrate
This is the answer to "how do you rate limit across microservices?" Shared atomic state in Redis + Lua atomicity. TTL on the key means **no cleanup job** is needed — expiry is automatic.

### Acceptance criteria / tests
- `Redis_fixed_window_allows_and_blocks` (integration test against a real/Testcontainers Redis).
- `Redis_sliding_window_is_atomic_under_concurrency` — hammer with parallel tasks, assert count never exceeds limit.
- `InMemory_limiter_violates_limit_across_workers` (the negative case, proving the problem Redis solves).
- Latency benchmark captured in README.

### Interview takeaway
Atomicity is the crux. Be ready to explain why `GET`+`INCR` from the app races, and how Lua (or a `MULTI/EXEC` + `WATCH`, but Lua is cleaner) fixes it.

---

## Level 5 — Middleware with multi-key limits
**Time:** ~3–4 hrs · **Stack:** ASP.NET Core middleware + Redis · **Difficulty:** medium

### Goal
Real APIs enforce limits on several dimensions at once — per IP, per user, per API key. Build composable middleware that applies multiple limiters in one request pipeline and fails fast on the first violation.

### Deliverables
- A reusable rate-limiting middleware (or an `IEndpointFilter` / attribute) that accepts a policy: `{ keySelector, limit, window, algorithm }`.
- A policy registry so routes can opt into named policies (mirrors ASP.NET Core's `AddRateLimiter` policy model — build yours, then compare).
- Chain multiple limiters: evaluate IP → user → API key; **reject on the first** that fails; return which dimension was hit.
- Correct response headers on success and failure: `X-RateLimit-Limit`, `X-RateLimit-Remaining`, `X-RateLimit-Reset`, and `Retry-After` on 429.
- Distinct limits for **authenticated vs anonymous** requests.
- A **whitelist/bypass** for internal service tokens (skip limiting entirely).
- Integration tests with `WebApplicationFactory` + a test Redis.

### Acceptance criteria / tests
- `Applies_all_three_limiters_in_order`
- `Returns_429_on_first_violated_dimension_with_correct_headers`
- `Authenticated_user_gets_higher_limit_than_anonymous`
- `Whitelisted_service_token_bypasses_limiting`
- `Headers_present_on_successful_requests`

### Interview takeaway
Composition + fail-fast ordering (cheapest/most-likely-to-fail check first). Know the standard header semantics cold — interviewers probe on `Retry-After` vs `X-RateLimit-Reset`.

---

# Production Patterns

## Level 6 — Tiered limits & quota management
**Time:** ~5–7 days · **Stack:** Redis + PostgreSQL/EF Core · **Difficulty:** hard

### Goal
This is what separates a senior answer. Stripe, GitHub, and OpenAI all enforce **multiple concurrent windows** (per-minute *and* per-day *and* per-month) with plan-aware quotas and cost-based accounting.

### Deliverables
- Plan/quota schema in PostgreSQL via EF Core: per-tier `requests_per_minute`, `requests_per_day`, `tokens_or_credits_per_month`. Seed Free / Pro / Enterprise tiers.
- Layered enforcement: check **per-minute → per-day → per-month**, cheapest/shortest window first, short-circuit on first failure.
- **Cost-based quotas:** not every request costs 1. e.g. an image generation costs 10 credits, a basic call costs 1. The limiter debits a variable amount.
- **Quota reset jobs:** daily reset at midnight UTC, monthly reset at billing-cycle anchor. Implement as a hosted background service or an external scheduler; make resets idempotent.
- **Overage handling:** hard block for Free tier; metered overage (allow + record for billing) for paid tiers — make the policy configurable per plan.
- Response body identifies **which limit** was hit and a precise `Retry-After`.
- **Admin API** to manually adjust a user's quota (customer-success scenario), with an audit trail.

### Design notes
- Keep the short-window counters in Redis (hot path); keep plan definitions and long-term quota ledger in Postgres (source of truth). Reconcile carefully — decide whether the monthly counter lives in Redis (fast, needs durable backup) or Postgres (durable, slower).
- Cost-based debiting must stay atomic — extend the Lua script to accept a `cost` argument and reject if remaining < cost.

### Acceptance criteria / tests
- `Free_tier_blocked_at_lower_limit_than_pro`
- `Per_minute_limit_trips_before_daily`
- `Image_request_debits_10_credits`
- `Daily_quota_resets_at_midnight_utc` (use an injectable clock — never `DateTime.Now` directly)
- `Paid_tier_allows_metered_overage`
- `Admin_can_adjust_user_quota` (and it's audited)

### Interview takeaway
Multiple concurrent windows, cost-based accounting, durable-vs-hot state split, and idempotent resets. Mention the injectable clock — it's a tell for someone who's actually tested time-based logic.

---

## Level 7 — Gateway enforcement + observability
**Time:** ~5–7 days · **Stack:** YARP/Envoy/Nginx + Prometheus + Grafana + OpenTelemetry · **Difficulty:** hard

### Goal
Move enforcement **out of each application** and to the edge, then add the observability layer that makes rate limiting operable in production. The hardest question here is the failure mode: what happens when Redis is down?

### Deliverables
- Edge enforcement: implement at the gateway. In .NET, **YARP** (Yet Another Reverse Proxy) is the natural choice — add a custom middleware/transform that calls your Redis limiter before forwarding. (Alternatively Envoy or Nginx + a Lua/`limit_req` module — document the tradeoff.)
- **Prometheus metrics** via `prometheus-net` / OpenTelemetry metrics:
  - `rate_limit_requests_total{plan, endpoint, result}`
  - `rate_limit_rejected_total{plan, endpoint, dimension}`
  - `quota_used_ratio{plan}`
  - `redis_call_duration_seconds` (histogram)
- **Grafana dashboard** (checked into the repo as JSON): allowed vs rejected over time, per plan, per endpoint; quota utilisation; Redis latency.
- **Alerting rules:** rejection rate > 5%, quota exhausted for > 10 users, Redis p99 latency spike.
- **Distributed tracing** with OpenTelemetry: trace a request through the gateway and show which limiter triggered and why (span attributes for key, dimension, remaining).
- **Circuit breaker on Redis** (Polly v8 `ResiliencePipeline`): when Redis is unavailable, decide and implement **fail-open** (allow traffic, prioritise availability) vs **fail-closed** (block, prioritise protection). Make it configurable and **log loudly** when degraded.
- **Load test** (k6 or bombardier) verifying the gateway enforces correctly at ~10k rps.

### Key insight to demonstrate
The fail-open vs fail-closed decision is a **business risk choice**, not a technical default:
- Fail-open → a Redis outage can let abusive traffic through and overwhelm downstreams.
- Fail-closed → a Redis outage rejects legitimate traffic, an availability hit.
Many real systems fail-open for the limiter but keep a cheap **local in-process fallback** (back to your Level 1–3 limiter!) so they degrade gracefully instead of going fully open. Wiring your early levels in as the degraded-mode fallback is the satisfying payoff of the whole roadmap.

### Acceptance criteria / tests
- `Gateway_enforces_limit_before_forwarding`
- `Metrics_increment_on_allow_and_reject`
- `Trace_contains_limiter_decision_attributes`
- `Redis_down_fails_open_when_configured` / `..._fails_closed_when_configured`
- `Local_fallback_limiter_engages_on_redis_outage`
- k6 results captured: throughput, p99, reject accuracy at 10k rps.

### Interview takeaway
Edge enforcement (zero per-service overhead), the four golden metrics for a limiter, distributed tracing of the decision, and — above all — a crisp, defensible answer on Redis failure modes with graceful degradation.

---

## Cross-cutting concerns (apply from Level 4 onward)
These are the SDE-1 → SDE-2 differentiators. Weave them in as you go rather than bolting on at the end:

- **Clock injection:** never call `DateTime.Now` / `DateTimeOffset.UtcNow` directly in limiter logic. Inject `TimeProvider` (.NET 8) so time-based tests are deterministic.
- **Key cardinality:** watch out for unbounded key growth in Redis (one key per IP can explode under attack). Consider key TTLs, hashing, and memory limits.
- **Clock skew:** across instances, timestamps come from different machines. Prefer Redis server time (`TIME` command) inside Lua for the sliding window to avoid skew.
- **Hot keys:** a single very popular key (one celebrity user, one abusive IP) can hot-spot a Redis shard. Note sharding/local-cache mitigations.
- **Graceful degradation:** the local-fallback pattern from Level 7.
- **Header standards:** follow the IETF `RateLimit` header draft where practical; be consistent.
- **Idempotency of resets** and **observability** as first-class, not afterthoughts.

---

## Recommended references
- Microsoft docs: *Rate limiting middleware in ASP.NET Core* and the `System.Threading.RateLimiting` APIs.
- StackExchange.Redis docs (scripting / `ScriptEvaluateAsync`).
- Polly v8 resilience pipelines (for the Level 7 circuit breaker).
- Marc Brooker / AWS Builders' Library on token buckets and fairness.
- The IETF `RateLimit` header fields draft.

---

*Build order matters: each level assumes the previous one's concepts. By Level 7 your early in-memory limiters return as the graceful-degradation fallback — the whole roadmap composes into one system.*
