# Level 1 — Fixed Window Counter (study notes)

In-memory, single-process rate limiter. The goal of this level is not just to build a working
limiter — it's to *feel* the boundary-burst flaw and to state, precisely, the two reasons fixed
window isn't good enough for strict or distributed limits.

---

## 1. The algorithm in one paragraph

Every client key gets a counter and a "which window am I in?" timestamp. Time is sliced into
fixed, equal-width windows aligned to the wall clock (e.g. for a 10s window: `:00, :10, :20...`).
On each request we figure out the current window. If it's the same window we last saw for this
key, we increment and compare to the limit. If it's a *new* window, we reset the counter to zero
first (lazy reset — no background timer). Over the limit → `429` with `Retry-After`.

- **Memory:** `O(1)` per key — one counter, not a list of timestamps. This is fixed window's headline win over the sliding-window log (Level 2).
- **Files:** [FixedWindowRateLimiter.cs](./FixedWindowRateLimiter.cs), [RateLimitingMiddleware.cs](./RateLimitingMiddleware.cs), [Program.cs](./Program.cs).

---

## 2. `AlignToWindow` — the modulo trick explained

```csharp
private DateTimeOffset AlignToWindow(DateTimeOffset instant)
{
    var windowTicks = _options.Window.Ticks;
    var alignedTicks = instant.UtcTicks - (instant.UtcTicks % windowTicks);
    return new DateTimeOffset(alignedTicks, TimeSpan.Zero);
}
```

### What it does
Snaps any instant **down** to the start of the window it falls into:

| Input | 10s window → output |
|---|---|
| `12:00:07`     | `12:00:00` |
| `12:00:09.999` | `12:00:00` |
| `12:00:13`     | `12:00:10` |

It's rounding down to the nearest window boundary. The boundaries are fixed points shared by
*every* client — that's what "aligned" means.

### What a "tick" is
.NET measures time in **ticks**, where `1 tick = 100 nanoseconds`:
- 1 second = `10,000,000` ticks
- A 10-second `TimeSpan` → `Window.Ticks = 100,000,000`
- `instant.UtcTicks` = number of ticks from a fixed origin (`0001-01-01T00:00:00Z`) to that instant — just a big integer.

Working in ticks keeps everything **integer arithmetic** → exact, no floating-point rounding.

### The trick: `x - (x % w)`
`x % w` is the **remainder** — how far past the last boundary you are. Subtracting it snaps you
back onto that boundary.

Concrete example with small numbers (window `w = 10`, instant at tick `x = 27`):

```
x        = 27           current position
x % w    = 27 % 10 = 7  7 ticks past the last boundary
x - (x%w)= 27 - 7  = 20 ← start of the window [20, 30)

boundaries:   0      10      20      30      40
              |-------|-------|-------|-------|
                              ^27 lands here
                              └─ snaps down to 20
```

Anything from `20`–`29` returns `20`; at `30` the remainder resets to `0` and we jump to the next
window. This is the integer equivalent of `Math.Floor(x / w) * w`, done with `%` so there's no
division-to-float-and-back.

### Caveat worth knowing
Alignment is relative to **UTC ticks from year 1**, not a "nice" local boundary. For windows that
divide evenly into a day (1s, 10s, 60s, 1h) boundaries land on clean clock values like `:00`. A
weird window like 7s still produces equal-width, shared windows — they just won't line up with
anything human-meaningful. Fine for rate limiting; it's why production configs usually pick
windows that divide cleanly.

---

## 3. The boundary-burst flaw (the whole point of Level 1)

Because adjacent windows are **independent buckets**, a client can drain one bucket at the end of
its window and the next bucket at the start of the following window:

```
limit = 5 per 10s window

 window [12:00:00 .. 12:00:10)        window [12:00:10 .. 12:00:20)
 ───────────────────────────┐        ┌───────────────────────────
                  ●●●●●  (5 reqs at 12:00:09)
                                     ●●●●●  (5 reqs at 12:00:11)
                            └──┬──┘
                          ~2 seconds, 10 requests = 2× the limit
```

Both batches are individually legal, but together they're **2× the limit across a ~2-second span**.
This is captured as a *passing* test — `Demonstrates_boundary_burst` — i.e. the bug is documented,
not hidden. Level 2 (sliding window) makes the identical scenario correctly **block** the overage.

> One-liner for an interview: *"Fixed window allows up to 2× the limit at window edges because
> adjacent windows are independent counters."*

### Why anchoring matters (a bug we actually hit)
The first implementation anchored each window to the client's **first request** instead of the wall
clock. With that model, advancing 9s then 2s is only 11s from the anchor, so the second batch stayed
in the *same* window and got blocked — the boundary-burst test failed. Switching to wall-clock
alignment made the burst sharp and reproducible. **Lesson:** *where you anchor the window changes
the failure mode.* First-request-anchored windows smear the burst out; wall-clock-aligned windows
concentrate it at fixed global boundaries.

---

## 4. The other limitation: correct only within one process

The per-key `lock` makes the read-modify-write atomic **inside this app instance**. But each
instance keeps its own `ConcurrentDictionary`. Run two instances behind a load balancer and a
client gets its full allowance against *each* one → effective limit becomes `limit × instances`.

```
        ┌─────────────┐   client sees
client ─┤ LB / proxy  ├─► up to 2× limit
        └──────┬──────┘
        ┌──────┴───────┐
   instance A      instance B
   dict: count=5    dict: count=5     ← two independent counters, no shared truth
```

This is exactly the problem **Level 4** solves by moving the counter into Redis and making the
increment atomic there (Lua / `INCR`). Keep this distinction crisp — it's a different flaw from the
boundary burst:

| Flaw | Cause | Fixed by |
|---|---|---|
| Boundary burst (2× at edges) | Independent adjacent windows | Level 2 — sliding window |
| Limit × N instances | Per-process state, no shared truth | Level 4 — Redis + atomic Lua |

---

## 5. Concurrency & locking

```csharp
var counter = _counters.GetOrAdd(key, _ => new Counter());
lock (counter.SyncRoot) { /* check + increment */ }
```

- `ConcurrentDictionary` safely handles concurrent **adds** of new keys.
- A **per-key lock object** (`counter.SyncRoot`) serializes mutation of an *existing* counter.
- We deliberately avoid a **single global lock** — that would serialize unrelated keys against each
  other and destroy throughput. Each key's critical section is independent.
- The critical section is tiny (a comparison + an increment), so contention stays low.

Proven by the `Concurrent_requests_never_exceed_limit_within_one_process` test: 200 parallel
requests against one key admit *exactly* `limit`.

---

## 6. HTTP header semantics (interviewers probe this)

| Header | Meaning |
|---|---|
| `X-RateLimit-Limit` | The ceiling for the window. |
| `X-RateLimit-Remaining` | Requests left in the current window. |
| `X-RateLimit-Reset` | Unix epoch **seconds** when the window resets (GitHub/Twitter style). |
| `Retry-After` | Seconds to wait before retrying; sent **only on a 429** (RFC 9110). |

The distinction they like to ask about:
- **`Retry-After`** answers *"how long until I can try again?"*
- **`X-RateLimit-Reset`** answers *"when does my full quota come back?"*

For a fixed window these point at the same instant, but they are **not the same concept** —
under sliding window or token bucket they diverge. We round `Retry-After` **up** (`Math.Ceiling`)
so we never tell a client to retry *before* the window has actually reset.

Headers are written via `Response.OnStarting(...)` so they land on the success path too (where a
downstream endpoint writes the body), not just on the 429.

---

## 7. Design decisions worth defending

| Decision | Why |
|---|---|
| **`TimeProvider` injected, never `DateTimeOffset.UtcNow`** | Time-based tests run instantly & deterministically via `FakeTimeProvider` — no `Thread.Sleep`, no flakes. The roadmap calls this an SDE-1→SDE-2 tell. |
| **Wall-clock-aligned windows** | The textbook fixed window; makes the boundary burst real and reproducible. |
| **Per-key lock, not global** | Correct under concurrency without serializing unrelated keys. |
| **Lazy reset on access, no background timer** | Keeps the flaw visible and memory O(1) per key. |
| **Async `IRateLimiter` contract** | L1 is synchronous, but L4 (Redis) is a real network call — committing to async now means later levels are drop-in. |
| **Key = `ip:<addr>`** | Weakest possible identity (spoofable, collapses NAT'd users). L5/L6 move to per-user / per-API-key / composite keys. |

---

## 8. Gotcha observed while running

When demoing with `curl`, the limiter allowed only **4** requests to `/api/resource` before the
`429`, not 5 — because the boot-probe request to `/` consumed one token from the same IP bucket.
That's correct behaviour: `/` is *also* behind the middleware. There's no bypass/whitelist yet —
that's introduced in Level 5. Lesson: *everything behind the middleware counts against the limit.*

---

## 9. Tests (xUnit)

| Test | Asserts |
|---|---|
| `Allows_up_to_limit_within_window` | First `limit` requests pass, `Remaining` decrements. |
| `Blocks_when_limit_exceeded` | Request `limit+1` is blocked with a positive `RetryAfter`. |
| `Resets_after_window_elapses` | After advancing one window, a fresh allowance is granted. |
| `Keys_are_isolated_from_each_other` | Exhausting key A doesn't affect key B. |
| `Demonstrates_boundary_burst` | **The flaw** — 2× limit slips through across a window edge. |
| `Concurrent_requests_never_exceed_limit_within_one_process` | 200 parallel requests admit exactly `limit`. |
| `Successful_request_carries_rate_limit_headers` (integration) | `X-RateLimit-*` present on 200. |
| `Exceeding_limit_returns_429_with_retry_after` (integration) | 6th rapid request → 429 + `Retry-After`. |

---

## 10. Run it

```bash
# from the solution root
dotnet test                                                   # all 8 tests
dotnet run --project src/Level1.FixedWindow --urls http://localhost:5080

# in another terminal — watch the 429 and headers appear
for i in $(seq 1 6); do
  curl -s -D - -o /dev/null http://localhost:5080/api/resource \
    | grep -iE 'HTTP/|X-RateLimit|Retry-After'
done
```

Configurable via `appsettings.json` / env: `RateLimit:Limit` (default 5) and
`RateLimit:WindowSeconds` (default 10).

---

## Interview takeaway (say it cleanly)

> Fixed window is O(1) memory and trivially simple, but it has **two** distinct flaws: it allows up
> to **2× the limit at window boundaries** (fixed by sliding window), and its counter is
> **per-process**, so across N instances the real limit is N× (fixed by moving state into Redis with
> atomic increments). Naming both — and which level fixes which — is the differentiator.
