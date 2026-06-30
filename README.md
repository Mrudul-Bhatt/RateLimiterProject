# Rate Limiting in .NET — A Level-by-Level Build

A progressively harder, interview-oriented implementation of rate limiting in C# / .NET 8,
following [`rate-limiting-roadmap.md`](./rate-limiting-roadmap.md). Each level is runnable, tested,
and documents the *flaw it exposes* and the *flaw the next level fixes*.

## Solution layout

```
RateLimiterProject/
├── shared/RateLimiting.Abstractions/   # IRateLimiter + RateLimitResult — the contract every level implements
├── src/Level1.FixedWindow/             # L1: fixed-window counter (this level)
└── tests/Level1.Tests/                 # L1: unit + integration tests
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

*Next: Level 2 — sliding window log, which eliminates the boundary burst (at a memory cost).*
