# Level 2 — Sliding Window Log (study notes)

In-memory, single-process. Level 1 exposed the **boundary burst**; this level eliminates it by
keeping the actual timestamps and counting a *trailing* window that slides with every request. The
catch — and the thing to be able to articulate — is the price: **O(requests-in-window) memory per
key**, plus a **cleanup job**.

---

## 1. The algorithm in one paragraph

Each key owns a FIFO queue of request timestamps (unix ms). On every request: (1) evict timestamps
older than `now - window` from the front, (2) if what remains is below the limit, admit and append
`now`, else reject. The "window" is never a fixed calendar slot — it's always *the last N seconds,
measured right now*. Files: [SlidingWindowLogRateLimiter.cs](./SlidingWindowLogRateLimiter.cs),
[SlidingWindowLogCleanupService.cs](./SlidingWindowLogCleanupService.cs).

```
limit = 5, window = 10s

requests:   t=1  t=3  t=4  t=8  t=9        (5 in queue)
new req at t=11 →  evict ≤ (11-10)=1  →  drops t=1  →  4 remain  →  ADMIT, append t=11
new req at t=11 →  4+1 = 5 ... next one →  5 remain, at limit  →  REJECT
```

A slot frees only when its original request slides out the back (`oldest + window`), which is also
exactly the `Retry-After` we return.

---

## 2. Why the boundary burst is gone

In Level 1, two adjacent fixed windows were independent counters, so a client could spend `limit`
at `:09` and `limit` again at `:11`. Here, at `:11` the `:09` timestamps are still within the last
10 seconds, so they still count. There is no edge to straddle.

This is captured by `Eliminates_boundary_burst` — the **same scenario** that was a passing
("the bug") test in Level 1 now asserts the second batch is **blocked**. Run both levels' tests
back to back; the contrast is the whole point of Level 2.

---

## 3. The cost: O(requests) memory per key (measured)

Fixed window stores one counter per key forever. The sliding log stores one timestamp *per request
in the window*. Benchmark ([bench/MemoryBenchmark](../../bench/MemoryBenchmark)), 1,000 keys, huge
limit so nothing is evicted:

| reqs/key | total reqs | FixedWindow | SlidingLog | ratio |
|---|---|---|---|---|
| 10    | 10,000     | ~410 KB | 762 KB  | 1.8× |
| 100   | 100,000    | ~410 KB | 2.5 MB  | 6.1× |
| 1,000 | 1,000,000  | ~410 KB | 16.1 MB | 40.2× |

FixedWindow is flat; SlidingLog grows linearly with traffic. Under real load this is why a pure log
is rarely shipped as-is. Two standard mitigations:

- **Sliding-window *counter*** (a hybrid): keep two fixed-window counters and interpolate. ~O(1)
  memory, approximately as accurate. Common production sweet spot.
- **Redis sorted sets** (Level 4): same log idea, but the store handles memory + expiry + atomicity.

> Note: in practice the per-key cost is capped at `limit` (once full, further requests are rejected
> and never stored). The benchmark uses an unbounded limit to expose the growth — but `limit` itself
> can be large, so the concern is real.

---

## 4. The cleanup job (what fixed window never needed)

A one-shot client (think: a flood of unique IPs during an attack) leaves behind a queue and a
dictionary key. Fixed window's idle counter was one tiny struct; a log is heavier and the key set
can grow without bound. So we add [SlidingWindowLogCleanupService](./SlidingWindowLogCleanupService.cs),
a `BackgroundService` that periodically calls `RemoveExpiredKeys()` to evict stale timestamps and
drop now-empty keys.

- Driven by a `PeriodicTimer(interval, TimeProvider)` so the interval is **testable** — the test
  advances a `FakeTimeProvider` to fire a sweep (`Cleanup_service_removes_idle_keys`).
- A failing sweep is caught and logged; it must never crash the host.

**Subtle race worth mentioning in an interview:** a request that already did `GetOrAdd` but is
still waiting on the per-key lock could enqueue into an entry the sweep removes — orphaning that one
timestamp. We use the reference-checked `TryRemove(KeyValuePair)` so we never delete an entry that
was refreshed after we emptied it, which shrinks but doesn't fully close the window. Cleanly solving
shared-mutable-state expiry is genuinely hard — which is exactly why **Redis TTLs (Level 4)** are so
attractive: expiry becomes the datastore's job and this whole loop disappears.

---

## 5. Still single-process (unchanged from L1)

The per-key `lock` serializes mutation within this instance, but each instance keeps its own
dictionary. Across N instances the effective limit is still `limit × N`. Sliding window fixes
*accuracy at the edges*, not *distribution*. Distribution is Level 4.

| Flaw | Status after L2 |
|---|---|
| Boundary burst (2× at edges) | **Fixed** — window slides |
| O(requests) memory per key | **Introduced** — the cost of precision |
| Needs a cleanup job | **Introduced** — keys don't self-expire |
| Limit × N across instances | Still open → Level 4 (Redis) |

---

## 6. Data-structure choice: why `Queue<long>`

- Requests arrive in time order, so timestamps are always enqueued ascending. Expired entries are
  therefore a **contiguous prefix** — eviction is a cheap `while (Peek() <= threshold) Dequeue()`,
  amortised O(number evicted), not a full scan.
- `Queue<T>` is a ring buffer internally: O(1) enqueue/dequeue, good cache locality.
- We store unix **milliseconds** (`long`) rather than `DateTimeOffset` structs — 8 bytes each,
  cheaper to keep millions of.
- A sorted set / skip list would be needed only if requests could arrive out of order (they can't
  here). That generalisation is what Redis `ZADD`/`ZREMRANGEBYSCORE` gives us in Level 4.

---

## 7. Seeing it work at runtime

Same three layers as Level 1 ([RateLimitingMiddleware.cs](./RateLimitingMiddleware.cs) logging,
`GET /debug/state`, dashboard at `/`), adapted:

- **State table** shows `count in window`, `oldest age`, and `remaining` per key.
- **Timeline** drops the fixed boundary lines and instead shades the **trailing window** — a band
  that slides with "now". A request is blocked when that band already holds `limit` dots.

**The demo that sells it:** Send 5 (fill), wait 1 second, Send 5 again. In Level 1 the second batch
slipped through; here it stays red, because the first batch is still inside the sliding band.

---

## 8. Tests (xUnit)

| Test | Asserts |
|---|---|
| `Allows_up_to_limit_within_window` | First `limit` requests pass, `Remaining` decrements. |
| `Blocks_when_limit_exceeded` | Over-limit request blocked with positive `RetryAfter`. |
| `Eliminates_boundary_burst` | **The payoff** — the L1 burst scenario now blocks. |
| `Resets_as_oldest_entries_slide_out` | After a full window elapses, capacity returns. |
| `Evicts_stale_timestamps` | Old timestamps drop out; in-window count reflects only recent. |
| `Keys_are_isolated_from_each_other` | Exhausting key A doesn't affect key B. |
| `RemoveExpiredKeys_drops_fully_expired_idle_keys` | Cleanup reclaims empty keys, not live ones. |
| `Concurrent_requests_never_exceed_limit_within_one_process` | 200 parallel admit exactly `limit`. |
| `Cleanup_service_removes_idle_keys` | The hosted service sweeps idle keys on its timer. |

---

## 9. Run it

```bash
dotnet test tests/Level2.Tests                                   # 9 tests
dotnet run --project src/Level2.SlidingWindowLog --urls http://localhost:5081
dotnet run --project bench/MemoryBenchmark -c Release            # the memory table above
```

Then open **http://localhost:5081/**. Configurable via `RateLimit:Limit` / `RateLimit:WindowSeconds`.

---

## Interview takeaway (say it cleanly)

> A sliding-window log is exact — it kills the fixed window's boundary burst because the window
> slides instead of resetting. The price is O(requests-in-window) memory per key plus a cleanup job
> for idle keys. In production you'd usually reach for the sliding-window *counter* hybrid (O(1),
> nearly as accurate) or push the log into Redis sorted sets, which also gives you atomicity and
> TTL-based expiry across instances.
