# Level 3 — Token Bucket & Leaky Bucket (study notes)

In-memory, single-process. Levels 1–2 counted requests inside a *window*. This level switches mental
model entirely: think of a **bucket**. Two algorithms, same bursty input, deliberately identical
`(capacity, rate)` — the only variable is the algorithm, and the outputs diverge sharply.

Both are O(1) state per key (one number + one timestamp) — no per-request log, no cleanup job.

---

## 1. Token bucket — "allow bursts, limit the average"

A bucket holds up to `Capacity` tokens and refills at `RefillPerSecond`. Every request spends 1
token; no token ⇒ reject. The bucket starts full, so an idle client can fire a burst of up to
`Capacity` at once, then is throttled to the refill rate. File:
[TokenBucketRateLimiter.cs](./TokenBucketRateLimiter.cs).

**Lazy refill** (no timer): on each request, add `(now - lastRefill) * rate` tokens, capped at
capacity. State per key is exactly the roadmap's `{ double Tokens, DateTimeOffset LastRefill }`.

```
capacity 5, refill 2/s, bucket starts full
burst of 5 at t=0  -> 5 tokens spent -> all ACCEPTED (burst absorbed)
6th at t=0         -> 0 tokens        -> REJECTED
wait 2.5s          -> +5 tokens (2/s) -> 5 more ACCEPTED
```

This is the most common API limiter (AWS, Stripe-style): it tolerates occasional spikes while
bounding the long-run average.

---

## 2. Leaky bucket — "smooth the output to a constant rate"

A bucket that leaks at a constant `LeakPerSecond`. Requests pour in; they drain out the bottom at
the fixed rate. If more than `Capacity` are already waiting, the newcomer overflows and is
**dropped**. File: [LeakyBucketRateLimiter.cs](./LeakyBucketRateLimiter.cs).

**Implementation: virtual scheduling (GCRA).** Instead of storing a queue, we keep one timestamp
per key — the **TAT** (theoretical arrival time), i.e. when the *next* request would be released.
With interval `T = 1/rate`:

```
scheduledRelease = max(TAT, now)          // never release in the past
queueDelay       = scheduledRelease - now // how long this request waits
if queueDelay <= (Capacity-1) * T:        // room in the bucket
    accept;  TAT = scheduledRelease + T   // push the tail one interval out
else:
    drop (overflow)
```

Because `TAT` advances by exactly `T` on every accept, releases come out spaced `T` apart — that is
the **smooth output**, regardless of how bursty the arrivals were. `RateLimitResult.ResetsAt`
carries each accepted request's scheduled release instant, which the harness/dashboard visualise.

Why GCRA over a literal `Queue<Request>`? O(1) state instead of O(capacity), no dequeue timer, and
it's the same algorithm ATM networks and Redis's `CL.THROTTLE` use. A literal queue is easier to
picture but heavier; documenting the trade is the point.

---

## 3. The contrast (captured — `dotnet run --project bench/BucketComparison -c Release`)

Identical config (capacity 5, rate 2/s). **Scenario A: a burst of 10 all arriving at t=0.**

```
req arrival   TOKEN BUCKET          LEAKY BUCKET
1   0ms       ACCEPT  out@0ms       ACCEPT  out@0ms
2   0ms       ACCEPT  out@0ms       ACCEPT  out@500ms
3   0ms       ACCEPT  out@0ms       ACCEPT  out@1,000ms
4   0ms       ACCEPT  out@0ms       ACCEPT  out@1,500ms
5   0ms       ACCEPT  out@0ms       ACCEPT  out@2,000ms
6-10 0ms      reject                DROP (overflow)

  output timeline (each dot = one request released downstream):
  token: #.............................     <- all 5 leave at once (BURSTY)
  leaky: o....o....o....o....o.........     <- one every 500ms (SMOOTH)
```

**Key subtlety most people miss:** on a *single instantaneous burst* both admit the same count
(capacity). They are duals for **admission**. The difference is **output shaping** — token bucket
releases the burst immediately; leaky bucket paces it. **Scenario B** (input already paced at the
rate) shows them behaving identically — the algorithms only diverge under burst.

---

## 4. When to pick which (interview gold)

| | Token bucket | Leaky bucket |
|---|---|---|
| Optimises | average rate, tolerates bursts | constant output rate |
| Output on a burst | bursty (up to capacity at once) | smooth (paced at leak rate) |
| Overflow | rejected | dropped (or queued) |
| Typical use | public API limits (AWS, Stripe) | shielding a fragile downstream (DB, 3rd-party at X/s) |
| State/key | `{ tokens, lastRefill }` | `{ tat }` (GCRA) |

Be able to **draw both** and give the one-line pick: *token bucket to bound average while allowing
spikes; leaky bucket to guarantee a downstream never sees more than X/sec.*

---

## 5. Still single-process (unchanged from L1/L2)

Per-key lock ⇒ correct within one instance only; across N instances the effective limit multiplies.
Buckets don't change that — distribution is still **Level 4** (Redis + atomic Lua). Buckets *do*
fix Level 2's two costs though: back to O(1) memory per key, and no cleanup job.

---

## 6. Seeing it work at runtime

- **Endpoints:** `GET /api/token` and `GET /api/leaky` (same client key, same config).
- **`GET /debug/state`:** both buckets' live internals — token *tokens available*, leaky *queue depth*.
- **Dashboard** at `/`: two columns. Fire the same traffic at both; watch the token level crash to
  zero on a burst (the burst got through) and climb back at the refill rate, while the leaky queue
  fills then drains linearly. Accept (green) / reject (red) dots run along the bottom of each graph.

---

## 7. Tests (xUnit)

| Test | Asserts |
|---|---|
| `TokenBucket_allows_burst_up_to_capacity` | Full bucket admits a burst of `capacity`, then blocks. |
| `TokenBucket_refills_over_time` | After draining, `rate × elapsed` tokens become available. |
| `TokenBucket_caps_accrual_at_capacity` | Long idle doesn't over-fill beyond capacity. |
| `TokenBucket_limits_long_run_average_to_refill_rate` | Steady-state admissions ≈ refill rate. |
| `LeakyBucket_drops_overflow` | Burst of 8 into capacity 5 ⇒ 5 accepted, 3 dropped. |
| `LeakyBucket_enforces_constant_output_rate` | Capacity 1 ⇒ exactly one admission per interval. |
| `LeakyBucket_smooths_a_burst_into_evenly_spaced_releases` | Accepted burst's release times are `T` apart. |
| `LeakyBucket_recovers_capacity_as_it_drains` | Room returns at the leak rate. |
| `Concurrent_requests_never_exceed_capacity_within_one_process` | 200 parallel admit exactly `capacity`. |

---

## 8. Run it

```bash
dotnet test tests/Level3.Tests                                   # 9 tests
dotnet run --project bench/BucketComparison -c Release           # the contrast above
dotnet run --project src/Level3.Buckets --urls http://localhost:5082
```

Then open **http://localhost:5082/**. Config: `RateLimit:Capacity`, `RateLimit:RatePerSecond`.

---

## Interview takeaway (say it cleanly)

> Token bucket and leaky bucket both bound the average rate, and on a single burst they admit the
> same count. The difference is output shaping: token bucket lets a burst through at once (great for
> APIs that want to tolerate spikes), leaky bucket releases at a fixed cadence (great for protecting
> a downstream that can only take X/sec). I'd implement leaky bucket with GCRA — O(1) state, and it's
> the same math ATM and Redis's throttle use.
