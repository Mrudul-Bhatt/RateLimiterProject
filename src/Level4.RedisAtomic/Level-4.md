# Level 4 — Redis-backed Atomic Counter (study notes)

This is the level that fixes the caveat every earlier level carried: **"correct only within a single
process."** State moves out of process memory into **Redis**, shared by all app instances, and the
read-check-increment runs **atomically** as a Lua script on the Redis server.

Stack: Redis 7 (Docker Compose), `StackExchange.Redis`, Lua, Testcontainers for integration tests.

---

## 1. The problem being solved (proven, not asserted)

Every in-memory limiter (L1–L3) keeps per-key state in a `ConcurrentDictionary`. Run two instances
behind a load balancer and each has its OWN dictionary, so a client gets its full allowance against
*each* — effective limit = `limit × instances`.

`bench/RaceConditionDemo` (and `CrossInstanceComparisonTests`) demonstrate it:

```
Rate limit: 10 requests / 30s per client   (sprayed across TWO instances)
  IN-MEMORY (2 instances):  20 allowed  ->  LIMIT VIOLATED (got 20, wanted 10)
  REDIS     (2 instances):  10 allowed  ->  held exactly at the limit
```

Shared state (Redis) is necessary but **not sufficient** — you also need atomicity, or you trade the
in-memory race for a distributed one.

---

## 2. Why Lua (the crux of the level)

Doing it from the app in three round trips —

```
GET count            -> 4          (instance A and instance B both read 4)
check 4 < 5          -> ok         (both decide they're under the limit)
INCR                 -> 5, then 6  (both increment; two admitted where one slot existed)
```

— is a classic **TOCTOU** (time-of-check to time-of-use) race *between instances*. `WATCH`/`MULTI`
/`EXEC` can fix it with optimistic retries, but a **Lua script is cleaner**: Redis runs it
single-threaded and atomically, so read + check + increment + expire happen as one indivisible
server-side operation, in **one round trip**. Scripts are shipped as embedded resources
(`Scripts/*.lua`) and executed with `ScriptEvaluateAsync`.

---

## 3. Fixed window — `Scripts/fixed_window.lua`

```lua
local current = tonumber(redis.call('GET', KEYS[1]) or '0')
if current + 1 > limit then ... return {0, 0, ttl} end     -- over limit: do NOT incr
current = redis.call('INCR', KEYS[1])
if current == 1 then redis.call('PEXPIRE', KEYS[1], windowMillis) end  -- TTL only on first
return {1, limit - current, ttl}
```

- **TTL only on the first increment** — so the window is a true fixed window (the whole window
  shares one expiry), not a sliding one.
- **We check before incrementing**, so the counter never exceeds the limit and `remaining` never
  goes negative (a naive INCR-then-check lets the counter run away under overload).
- **The TTL is the reset** — and it also expires idle keys, so unlike Level 2 there is **no cleanup
  job**. Seen live in `/debug/state`: the key's `ttlSeconds` counts down, then the key vanishes.

Inherits Level 1's boundary-burst flaw (it's still a fixed window) — just now correct across instances.

---

## 4. Sliding window — `Scripts/sliding_window.lua`

Level 2's per-key timestamp log, distributed, using a Redis **sorted set** (member per request,
score = timestamp):

```lua
redis.call('ZREMRANGEBYSCORE', KEYS[1], 0, now - window)   -- evict old
local count = redis.call('ZCARD', KEYS[1])                 -- count what's left
if count < limit then
    redis.call('ZADD', KEYS[1], now, member)               -- record
    redis.call('PEXPIRE', KEYS[1], window)                 -- self-expire idle keys
    return {1, limit - count - 1, 0}
end
```

- Eliminates the boundary burst (same as Level 2), now across instances.
- `PEXPIRE` removes the need for Level 2's cleanup `IHostedService` entirely.
- Member is `"{now}-{guid}"` so simultaneous requests don't collide on the same score.

---

## 5. Clock: app time vs Redis server time (skew)

The sliding script takes `now` as an argument (from the app's `TimeProvider`). That keeps tests
**deterministic** — a `FakeTimeProvider` drives `now` into the script, so
`Redis_sliding_window_eliminates_boundary_burst` runs against a *real* Redis yet is fully
reproducible.

In production with many app hosts, their clocks drift. The skew-proof alternative is to read Redis's
own clock inside the script — `local t = redis.call('TIME')` — so all instances share one time
source. It's a one-line change; we favour the injectable clock here for testability and call out the
trade explicitly. (This is a listed cross-cutting concern in the roadmap.)

---

## 6. Latency — the price of correctness

From `bench/RaceConditionDemo`:

```
in-process : ~0.2 us
redis      : ~0.5 ms   (~1000-3000x slower)
```

A network round trip is orders of magnitude slower than a dictionary lookup — but still sub-
millisecond, and it buys a globally-correct limit. Level 7's fallback pattern (local limiter when
Redis is down) is the answer when even that round trip is too costly or Redis is unavailable.

---

## 7. What this level fixes / leaves open

| Concern | Status after L4 |
|---|---|
| Correct across N instances | **Fixed** — shared Redis state |
| Read-modify-write race | **Fixed** — Lua atomicity, one round trip |
| Cleanup job for idle keys | **Gone** — Redis TTL expiry |
| Boundary burst (fixed variant) | Still present in the fixed limiter (use the sliding one) |
| Clock skew across hosts | Mitigated via app clock; `redis.call('TIME')` for full safety |
| Redis is now a hard dependency / SPOF | Opens Level 7 — circuit breaker + local fallback (fail-open vs fail-closed) |

---

## 8. Tests (Testcontainers → real Redis; requires Docker)

| Test | Asserts |
|---|---|
| `Redis_fixed_window_allows_and_blocks` | 5 allowed then blocked, remaining decrements. |
| `Redis_fixed_window_resets_after_ttl_expires` | After the TTL, a fresh window is granted. |
| `Redis_fixed_window_is_atomic_under_concurrency` | 500 concurrent on one key → exactly `limit` admitted. |
| `Redis_sliding_window_allows_and_blocks` | Basic admit/deny. |
| `Redis_sliding_window_eliminates_boundary_burst` | L2 scenario, distributed + deterministic. |
| `Redis_sliding_window_is_atomic_under_concurrency` | 500 concurrent → exactly `limit`. |
| `InMemory_limiter_violates_limit_across_instances` | **The problem** — 2 in-memory instances admit 2×. |
| `Redis_limiter_holds_limit_across_instances` | **The fix** — 2 Redis instances admit exactly `limit`. |

---

## 9. Run it

```bash
docker compose up -d                                             # Redis on localhost:6379
dotnet test tests/Level4.Tests                                   # 8 tests (spins its own Redis)
dotnet run --project bench/RaceConditionDemo -c Release          # race + latency demo
dotnet run --project src/Level4.RedisAtomic --urls http://localhost:5094
docker compose down                                              # stop Redis
```

Then open **http://localhost:5094/** — fire the fixed/sliding endpoints and watch the actual Redis
keys (count + TTL) in the state table. Open it in **two browser tabs** to feel the shared counter.
Config: `Redis:ConnectionString`, `RateLimit:Limit`, `RateLimit:WindowSeconds`.

---

## Interview takeaway (say it cleanly)

> To rate limit across instances you need shared state — Redis — but shared state alone just moves
> the race across the network. The fix is atomicity: a Lua script runs read-check-increment-expire
> as one indivisible server-side operation in a single round trip, so `GET`+`INCR` can't interleave
> between instances. TTL doubles as the window reset and as automatic cleanup of idle keys. The cost
> is a sub-millisecond round trip and a new dependency on Redis — which is exactly why Level 7 adds a
> circuit breaker with a local fallback.
