# Rate Limiting in .NET — A Level-by-Level Build

A progressively harder, interview-oriented implementation of rate limiting in C# / .NET 8,
following [`rate-limiting-roadmap.md`](./rate-limiting-roadmap.md). Each level is runnable, tested,
and documents the *flaw it exposes* and the *flaw the next level fixes*.

## Solution layout

```
RateLimiterProject/
├── shared/RateLimiting.Abstractions/   # IRateLimiter + RateLimitResult — the contract every level implements
├── src/Level1.FixedWindow/             # L1: fixed-window counter
├── src/Level2.SlidingWindowLog/        # L2: sliding-window log
├── src/Level3.Buckets/                 # L3: token bucket & leaky bucket
├── src/RateLimiting.Redis/             # reusable Redis limiters + Lua (shared by L4/L5)
├── src/Level4.RedisAtomic/             # L4: Redis-backed atomic counter (Lua)
├── src/Level5.Middleware/              # L5: composable multi-key middleware
├── docker-compose.yml                  # Redis (L4); Prometheus/Grafana added at L7
├── bench/MemoryBenchmark/              # L1 vs L2 memory footprint harness
├── bench/BucketComparison/            # L3 token-vs-leaky output comparison
├── bench/RaceConditionDemo/           # L4 in-memory-vs-Redis race + latency
└── tests/                              # Level1..Level4 .Tests
```

## Build & test

```bash
dotnet test                                   # run all tests
dotnet run --project src/Level1.FixedWindow   # run the API (defaults to http://localhost:5xxx)
```

---

## Level 1 — Fixed Window Counter ✅

**Stack:** in-memory, ASP.NET Core minimal API. No Redis.

### What it does
A `ConcurrentDictionary`-backed counter per client key (keyed on IP for now). Each key gets `Limit`
requests per wall-clock-aligned `Window`. Exceeding it returns `429 Too Many Requests` with a
`Retry-After` header. Standard headers (`X-RateLimit-Limit/Remaining/Reset`) are emitted on every
response.

### Design decisions worth defending in an interview
| Decision | Why |
|---|---|
| **`TimeProvider` injected, never `DateTimeOffset.UtcNow`** | Time-based tests run instantly and deterministically via `FakeTimeProvider` — no `Thread.Sleep`, no flakes. The roadmap calls this an SDE-1→SDE-2 tell. |
| **Wall-clock-aligned windows** (not first-request-anchored) | This is the textbook fixed window; it's the variant that makes the boundary burst real and reproducible. *Where you anchor the window changes the failure mode.* |
| **Per-key lock, not a global lock** | Correct under concurrency without serializing unrelated keys against each other. |
| **Lazy reset on access, no background timer** | Keeps the flaw visible and the code O(1) memory per key. |
| **Async `IRateLimiter` contract** | L1 is synchronous, but L4 (Redis) is a real network call. Committing to async now means later levels are drop-in. |

### The flaw (the whole point of Level 1)
**Boundary burst.** Because each window is independent, a client can send `Limit` requests at the
very end of one window and `Limit` more at the very start of the next — **2× the limit across a
~2-second span**. This is captured as a *passing* test: `Demonstrates_boundary_burst`.

### The other limitation (sets up Level 4)
Correct **only within a single process**. Two instances behind a load balancer each keep their own
dictionary, so the effective limit becomes `Limit × instances`. Redis + atomic Lua (Level 4) fixes this.

### Tests
- `Allows_up_to_limit_within_window`
- `Blocks_when_limit_exceeded`
- `Resets_after_window_elapses`
- `Keys_are_isolated_from_each_other`
- `Demonstrates_boundary_burst` — the flaw, captured as a passing test
- `Concurrent_requests_never_exceed_limit_within_one_process`
- `Successful_request_carries_rate_limit_headers` (integration)
- `Exceeding_limit_returns_429_with_retry_after` (integration)

### Interview takeaway
> Fixed window is O(1) memory and dead simple, but allows up to 2× burst at window edges, and is
> only correct within one process. State both flaws precisely — they motivate sliding window (L2)
> and Redis (L4) respectively.

---

## Level 2 — Sliding Window Log ✅

**Stack:** in-memory, ASP.NET Core minimal API. No Redis. Full notes: [Level-2.md](src/Level2.SlidingWindowLog/Level-2.md).

### What it does
Keeps a per-key FIFO queue of request timestamps. Each request evicts entries older than
`now - window`, then admits only if fewer than `Limit` remain. The window is always *the last N
seconds, measured now* — so there are no fixed edges to exploit.

### The payoff
**The boundary burst is eliminated.** The exact scenario that was a passing "bug" test in Level 1
(`Demonstrates_boundary_burst`) now correctly **blocks** the overage (`Eliminates_boundary_burst`).

### The cost (measured — `dotnet run --project bench/MemoryBenchmark -c Release`)
| reqs/key | total reqs | FixedWindow | SlidingLog | ratio |
|---|---|---|---|---|
| 10    | 10,000     | ~410 KB | 762 KB  | 1.8× |
| 100   | 100,000    | ~410 KB | 2.5 MB  | 6.1× |
| 1,000 | 1,000,000  | ~410 KB | 16.1 MB | 40.2× |

Memory is **O(requests-in-window) per key** vs fixed window's O(1). It also needs a **cleanup
`IHostedService`** to reclaim idle keys — maintenance fixed window never required.

### Tests (9)
`Eliminates_boundary_burst`, `Evicts_stale_timestamps`, `RemoveExpiredKeys_drops_fully_expired_idle_keys`,
`Cleanup_service_removes_idle_keys`, plus the standard allow/block/reset/isolation/concurrency set.

### Interview takeaway
> Sliding log is exact and kills the boundary burst, but costs O(requests-in-window) memory per key
> plus a cleanup job. In production prefer the sliding-window *counter* hybrid (O(1), ~as accurate)
> or push the log into Redis sorted sets (which also gives atomicity + TTL expiry across instances).

---

## Level 3 — Token Bucket & Leaky Bucket ✅

**Stack:** in-memory, ASP.NET Core minimal API. Full notes: [Level-3.md](src/Level3.Buckets/Level-3.md).

### What it does
Two algorithms side by side, given identical `(capacity, rate)` so the only variable is the
algorithm. Both are back to **O(1) state per key** (no per-request log, no cleanup job — the costs L2 introduced).

- **Token bucket** — tokens accrue up to capacity and refill lazily; a burst drains them fast.
  Absorbs bursts, bounds the average. State: `{ double Tokens, DateTimeOffset LastRefill }`.
- **Leaky bucket** — implemented via **GCRA / virtual scheduling** (O(1), one TAT timestamp per key).
  Accepts up to capacity but releases at a fixed cadence; overflow is dropped.

### The contrast (`dotnet run --project bench/BucketComparison -c Release`)
Same burst of 10 at t=0, capacity 5, rate 2/s:
```
  token: #.............................   <- all 5 leave at once (BURSTY output)
  leaky: o....o....o....o....o.........   <- one every 500ms (SMOOTH output)
```
**Subtlety:** on a single instantaneous burst both admit the *same count* (they're admission duals);
they differ in **output shaping** — token releases immediately, leaky paces it.

### Tests (9)
`TokenBucket_allows_burst_up_to_capacity`, `TokenBucket_refills_over_time`,
`LeakyBucket_drops_overflow`, `LeakyBucket_enforces_constant_output_rate`,
`LeakyBucket_smooths_a_burst_into_evenly_spaced_releases`, plus accrual-cap / average-rate / recovery / concurrency.

### Interview takeaway
> Both bound the average; on a burst they admit the same count. The difference is output shaping —
> token bucket lets bursts through (APIs tolerating spikes), leaky bucket paces output (protecting a
> fragile downstream). Implement leaky bucket with GCRA: O(1) state, same math as ATM / Redis throttle.

---

## Level 4 — Redis-backed Atomic Counter ✅

**Stack:** Redis 7 (Docker Compose), StackExchange.Redis, Lua, Testcontainers. Full notes: [Level-4.md](src/Level4.RedisAtomic/Level-4.md).

### What it does
Moves limiter state out of process memory into **Redis**, shared by all instances, and runs the
read-check-increment as an **atomic Lua script** on the server (one round trip, no TOCTOU race).
Two limiters: `RedisFixedWindowRateLimiter` (INCR + PEXPIRE) and `RedisSlidingWindowRateLimiter`
(sorted set: ZREMRANGEBYSCORE + ZCARD + ZADD). Scripts ship as embedded `.lua` resources.

### The payoff (`dotnet run --project bench/RaceConditionDemo -c Release`)
```
Rate limit: 10 / 30s, sprayed across TWO instances
  IN-MEMORY (2 instances):  20 allowed  ->  LIMIT VIOLATED
  REDIS     (2 instances):  10 allowed  ->  held exactly
Latency:  in-process ~0.2us   vs   redis ~0.5ms
```

### Two Level-2 costs disappear
- **No cleanup job** — Redis TTL (`PEXPIRE`) expires idle keys automatically.
- Plus the headline: correct across N instances, via Lua atomicity.

### Tests (8, Testcontainers → real Redis; needs Docker)
`Redis_fixed_window_is_atomic_under_concurrency` & sliding equivalent (500 concurrent → exactly the
limit), `InMemory_limiter_violates_limit_across_instances` (the problem) vs
`Redis_limiter_holds_limit_across_instances` (the fix), boundary-burst elimination, TTL reset.

### Interview takeaway
> Shared state alone just moves the race across the network; atomicity is the crux. A Lua script runs
> read-check-incr-expire as one indivisible server-side op in a single round trip. TTL doubles as the
> window reset and idle-key cleanup. Cost: a sub-ms round trip and a Redis dependency — which is why
> Level 7 adds a circuit breaker + local fallback.

---

## Level 5 — Middleware with Multi-Key Limits ✅

**Stack:** ASP.NET Core middleware + Redis (reuses the Level 4 limiters). Full notes: [Level-5.md](src/Level5.Middleware/Level-5.md).

### What it does
One middleware applies an ordered **chain** of limit policies — IP → anon → user → API key — and
**fails fast** on the first violated dimension, naming it in `X-RateLimit-Dimension` and the 429 body.
A policy is `{ Name, KeySelector, Limit, Window, Algorithm }`; a `null` key-selector skips the
dimension, so one chain serves anonymous, authenticated, and api-key traffic.

- **Auth-aware:** anonymous → tight per-IP limit; authenticated → higher per-user budget.
- **Whitelist bypass:** `X-Internal-Token` skips all limiting for internal callers.
- **Correct headers** on success and 429 (`X-RateLimit-Limit/Remaining/Reset/Dimension`, `Retry-After`).

### Tests (5, WebApplicationFactory + Testcontainers Redis)
`Applies_all_three_limiters_in_order`, `Returns_429_on_first_violated_dimension_with_correct_headers`,
`Authenticated_user_gets_higher_limit_than_anonymous`, `Whitelisted_service_token_bypasses_limiting`,
`Headers_present_on_successful_requests`.

### Interview takeaway
> Production limiting is multi-dimensional: a chain of policies, reject on the first violated one.
> Order cheapest / most-likely-to-fail first (fail-fast still charges earlier dimensions). A null
> key-selector cleanly skips a dimension. Know the header set — especially `Retry-After` vs `X-RateLimit-Reset`.

---

*Next: Level 6 — tiered limits & quota management (plans, concurrent per-minute/day/month windows, cost-based debits, and idempotent resets).*
