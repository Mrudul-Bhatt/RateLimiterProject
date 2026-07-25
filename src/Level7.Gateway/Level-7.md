# Level 7 — Gateway Enforcement + Observability (study notes)

The finale. Enforcement moves **out of each app and to the edge** (a gateway), and we add the
operational layer that makes rate limiting runnable in production: metrics, tracing, and — the
hardest interview question — a **defensible answer to "what happens when Redis is down?"**

Stack: YARP (reverse proxy) + Polly v8 (circuit breaker) + prometheus-net + OpenTelemetry.

---

## 1. Edge enforcement

Levels 1–6 enforced *inside* each service. Here a single **gateway** enforces once, at the front
door, then forwards the survivors to the backend — zero per-service overhead, one place to reason
about limits. [GatewayRateLimitingMiddleware.cs](./GatewayRateLimitingMiddleware.cs) runs **before**
the proxy, so a rejected request returns `429` and the **backend is never touched**
(`Gateway_enforces_limit_before_forwarding`, and the backend's own hit counter stays put).

Forwarding is **YARP** (`MapReverseProxy`), configured from the `ReverseProxy` section (see
`appsettings.Proxy.json`). With no proxy config, a fallback echo endpoint stands in for the backend
so the gateway runs self-contained (that's what the tests use). Alternatives — Envoy, or Nginx +
`limit_req` — are the same pattern at a different layer; YARP keeps it in-process and in C#.

---

## 2. The hard part: Redis is down

The primary limiter is the Level 4 Redis one. But Redis is now a hard dependency on the hot path, so
we wrap it in a **Polly v8 circuit breaker** ([ResilientRateLimiter.cs](./ResilientRateLimiter.cs)).
While Redis is healthy, calls pass through and we time them. When Redis errors — or the breaker trips
open after repeated failures — we stop hammering it and **degrade** per a configured policy:

| `DegradeMode` | Behaviour on outage | Trade-off |
|---|---|---|
| **FailOpen** | allow everything | availability over protection — abuse can flood the backend |
| **FailClosed** | reject everything | protection over availability — legit traffic is dropped |
| **LocalFallback** | fall back to an in-process limiter | graceful: a rough per-instance cap still holds |

**This is a business risk choice, not a technical default.** State it that way. Tests pin all three:
`Redis_down_fails_open_when_configured`, `..._fails_closed_...`, `Local_fallback_limiter_engages_on_redis_outage`.

### The payoff of the whole roadmap
`LocalFallback` degrades to [InProcessFallbackLimiter.cs](./InProcessFallbackLimiter.cs) — which is
**Level 1's fixed-window algorithm**, running in-process. It can't hold a *global* limit (that's why
we went to Redis at L4), but during an outage a rough per-instance cap beats going fully open. The
early in-memory levels return as the degraded-mode safety net — the satisfying close of the series.

The circuit breaker also means a Redis outage doesn't add a timeout to *every* request: after a few
failures the breaker opens and degrade decisions are instant until the cool-off retry.

---

## 3. The four golden metrics (prometheus-net, `/metrics`)

[GatewayTelemetry.cs](./GatewayTelemetry.cs), scraped by Prometheus:

- `rate_limit_requests_total{endpoint,result,source}` — every decision (source = redis | local_fallback | fail_open | fail_closed)
- `rate_limit_rejected_total{endpoint,reason}` — rejections (reason = over_limit | fail_closed)
- `redis_call_duration_seconds` — histogram of the Redis round trip (for p99)
- `rate_limit_circuit_open` — 1 while degraded, for alerting

`Metrics_increment_on_allow_and_reject` scrapes `/metrics` and asserts the counters move.

**Grafana dashboard** (`config/grafana/dashboards/rate-limiter.json`, provisioned) and **alert rules**
(`config/prometheus/alerts.yml`): rejection rate > 5%, circuit open (degraded), Redis p99 > 50ms.

---

## 4. Distributed tracing (OpenTelemetry)

The middleware opens a `rate_limit.check` span per request and tags it with the decision — `key`,
`result`, `source`, `remaining`, `degraded` — so a trace shows *which* limiter triggered and *why*
(`Trace_contains_limiter_decision_attributes`). Spans flow through an `ActivitySource` exported via
OpenTelemetry (console here; swap for OTLP → Jaeger/Tempo in prod).

---

## 5. Load test (k6)

`bench/k6/load-test.js` ramps arrival rate and checks the gateway sheds load cleanly (200 or 429,
never 5xx) with p99 < 50ms. Bump the stages toward ~10k rps. Run the gateway, then `k6 run`.

---

## 6. Tests (WebApplicationFactory, no containers needed)

Level 7's new logic is resilience/metrics/tracing, all deterministic with a **stub** primary limiter
(allow / block / throw-to-simulate-outage) swapped in via keyed DI — so these tests are fast and need
no Redis.

| Test | Asserts |
|---|---|
| `Gateway_enforces_limit_before_forwarding` | Blocked → 429, backend echo never invoked. |
| `Allowed_request_is_forwarded_to_backend` | Allowed → reaches the backend. |
| `Metrics_increment_on_allow_and_reject` | `/metrics` shows the request/reject counters. |
| `Trace_contains_limiter_decision_attributes` | The span carries `result` / `source` / `key`. |
| `Redis_down_fails_open_when_configured` | Outage + FailOpen → allowed (`source=fail_open`). |
| `Redis_down_fails_closed_when_configured` | Outage + FailClosed → 429 (`source=fail_closed`). |
| `Local_fallback_limiter_engages_on_redis_outage` | Outage + LocalFallback → in-process cap holds. |

---

## 7. Interactive dashboard — testing every scenario without touching Docker

Levels 1–6 each had a `wwwroot/index.html` you could click through. Level 7's is different in one
important way: the *interesting* scenarios here aren't "send a burst" (you've seen that six times) —
they're "what does the gateway do while Redis is unreachable?" Normally that means `docker stop
ratelimiter-redis` mid-demo, which is awkward to narrate and easy to forget to undo.

So the dashboard (served at `/`) adds two **runtime toggles**, backed by
[RuntimeControls.cs](./RuntimeControls.cs) and two POST endpoints:

- **`POST /debug/simulate-outage { enabled }`** — flips `OutageSimulator.Enabled`. When on,
  `ResilientRateLimiter` throws *inside* the same Polly pipeline delegate a real Redis exception
  would hit — so the circuit breaker trips for real, `rate_limit_circuit_open` goes to 1, and the
  exact same code path runs as a genuine outage. Nothing about the failure handling is faked; only
  the trigger is.
- **`POST /debug/degrade-mode { mode }`** — flips `DegradeModeSwitch.Mode` live, so you can change
  fail-open → fail-closed → local-fallback *while already degraded* and watch the same failure
  handled three different ways, no restart.

**A full scripted demo, verified end-to-end:**

```bash
curl -s http://localhost:5097/api/resource                                          # source=redis
curl -sX POST -d '{"enabled":true}'  -H 'Content-Type: application/json' \
  http://localhost:5097/debug/simulate-outage
for i in 1 2 3; do curl -s http://localhost:5097/api/resource -D - -o /dev/null \
  | grep X-RateLimit-Source; done                                                   # trips after ~3
curl -s http://localhost:5097/debug/state | grep circuitOpen                        # true
curl -sX POST -d '{"mode":"FailClosed"}' -H 'Content-Type: application/json' \
  http://localhost:5097/debug/degrade-mode                                          # switch live
curl -s http://localhost:5097/api/resource -D - -o /dev/null | grep -E '429|source'
curl -sX POST -d '{"enabled":false}' -H 'Content-Type: application/json' \
  http://localhost:5097/debug/simulate-outage                                       # "Redis recovers"
sleep 6                                                                              # past BreakDuration
curl -s http://localhost:5097/api/resource -D - -o /dev/null | grep X-RateLimit-Source  # back to redis
```

That's every acceptance-test scenario, driven live, in one sequence. The dashboard wraps this in
buttons: a status panel (circuit state / mode / outage flag), burst controls, a mode selector, and a
running log where each row's chip shows `redis` / `local_fallback` / `fail_open` / `fail_closed`.

**For the fully authentic version** (no simulation, the real container down): `docker stop
ratelimiter-redis`, drive traffic, then `docker start ratelimiter-redis` and watch it recover. The
simulator exists so you don't *have to* do that mid-demo — but doing it for real once is worth it to
convince yourself the simulator isn't cheating.

---

## 8. Run it

```bash
docker compose up -d                                                    # Redis, Postgres, Prometheus, Grafana
dotnet run --project src/Level7.Backend  --urls http://localhost:5099   # the protected backend
ASPNETCORE_ENVIRONMENT=Proxy \
  dotnet run --project src/Level7.Gateway --urls http://localhost:5097  # gateway (forwards to backend)
```

Open **http://localhost:5097/** for the dashboard, or drive it from the shell:

```bash
curl http://localhost:5097/api/resource        # 200 (forwarded) until the limit, then 429 at the edge
open http://localhost:9090                      # Prometheus
open http://localhost:3000                      # Grafana (admin/admin) → "Rate Limiter — Gateway"
```

For a heavier, sustained-throughput check, use k6 instead of clicking buttons:

```bash
k6 run bench/k6/load-test.js                    # ramps toward ~10k rps against the gateway
```

Config: `Gateway:{Limit,WindowSeconds,DegradeMode}`, `Gateway:Fallback:Limit`, `Redis:ConnectionString`,
and the `ReverseProxy` section for YARP routes/clusters.

---

## Interview takeaway (say it cleanly)

> Enforce at the edge (a YARP gateway) so limiting is one hop with zero per-service cost. Instrument
> it with the four golden metrics and trace each decision so you can see which limiter fired and why.
> And have a crisp answer for a Redis outage: it's a business risk choice — fail-open (availability),
> fail-closed (protection), or best of both, a Polly circuit breaker that degrades to a cheap
> in-process limiter so you shed abuse without a full outage. That in-process fallback is literally
> the fixed-window limiter from Level 1 — the whole roadmap composing into one graceful system.
