# Level 5 — Middleware with Multi-Key Limits (study notes)

Real APIs don't limit on one thing — they cap **per IP AND per user AND per API key** at once. This
level builds composable middleware that evaluates an ordered **chain** of limit policies and fails
fast on the first violated dimension. It reuses Level 4's Redis limiters as the enforcement engine.

Stack: ASP.NET Core middleware + Redis. Tests: `WebApplicationFactory` + Testcontainers Redis.

---

## 1. The policy model

A **policy** is one dimension: `{ Name, KeySelector, Limit, Window, Algorithm }`
([RateLimitPolicy.cs](./RateLimitPolicy.cs)).

The clever bit is `KeySelector: HttpContext -> string?`. It returns the key value for the dimension,
or **`null` to mean "not applicable to this request."** A null selector makes the middleware **skip**
that policy — which is how a single ordered chain serves anonymous, authenticated, and api-key
traffic without branching:

```csharp
ip:     ctx => ClientIdentity.Ip(ctx)                                   // always applies
anon:   ctx => IsAuthenticated(ctx) ? null : ClientIdentity.Ip(ctx)     // only when NOT logged in
user:   ctx => IsAuthenticated(ctx) ? ClientIdentity.UserId(ctx) : null // only when logged in
apikey: ctx => ClientIdentity.ApiKey(ctx)                               // only if X-Api-Key sent
```

A **registry** ([RateLimitPolicyRegistry.cs](./RateLimitPolicyRegistry.cs)) holds the ordered chain
— this mirrors ASP.NET Core's own `AddRateLimiter().AddPolicy()` model; we build ours to understand
it, then compare.

---

## 2. The middleware: chain + fail-fast

[CompositeRateLimitingMiddleware.cs](./CompositeRateLimitingMiddleware.cs):

```
1. /debug/* and static files are never limited.
2. Whitelist bypass: X-Internal-Token matches the secret -> skip ALL limiting.
3. For each policy in order:
     key = policy.KeySelector(ctx)
     if key is null: skip (dimension not applicable)
     result = redisLimiter(policy).CheckAsync(key)
     if blocked: 429 immediately, naming this dimension   <-- FAIL FAST
     else: remember it if it's the tightest so far
4. All applicable dimensions allowed -> headers from the tightest -> continue.
```

### Which dimension's numbers go in the headers?
On success we report the **most constrained** applicable dimension (least `Remaining`) — that's the
limit the client will hit first, so it's the most useful thing to tell them. `X-RateLimit-Dimension`
names it. On a 429 we report the dimension that actually blocked.

### The fail-fast trade-off (interview point)
Because we increment each dimension's counter as we go, a request blocked by a **later** dimension
has already "charged" the earlier ones. That over-counting is inherent to fail-fast multi-dimension
limiting. You minimise the waste by ordering **cheapest / most-likely-to-fail first**. (The roadmap's
IP → user → key order is the conventional one; note the tension and pick deliberately.)

---

## 3. Authenticated vs anonymous

Handled entirely by the mutually-exclusive `anon` and `user` policies:
- **Anonymous** request → `anon` applies (keyed by IP), a deliberately tight limit.
- **Authenticated** request → `user` applies (keyed by user id), a higher per-user budget; `anon` is
  skipped.

So an authenticated user genuinely gets a **higher** limit than an anonymous one — proven by
`Authenticated_user_gets_higher_limit_than_anonymous` (anon blocked at 3, user allowed 8).

Auth itself is faked by [SimulatedAuthMiddleware](./ClientIdentity.cs): an `X-User-Id` header becomes
an authenticated `ClaimsPrincipal`. A real app validates a JWT/cookie and populates
`HttpContext.User` — the rate-limiting code downstream is identical.

---

## 4. Whitelist bypass

A trusted internal caller sends `X-Internal-Token: <secret>` and skips limiting entirely — for
service-to-service calls that shouldn't count against public quotas. `Whitelisted_service_token_
bypasses_limiting` fires 20 requests against a limit of 2 and all 20 succeed.

---

## 5. Header semantics (interviewers probe this)

| Header | Meaning |
|---|---|
| `X-RateLimit-Limit` | Ceiling of the reported dimension. |
| `X-RateLimit-Remaining` | Remaining in that dimension. |
| `X-RateLimit-Reset` | Unix seconds when it resets. |
| `X-RateLimit-Dimension` | Which dimension these numbers (or the 429) refer to. |
| `Retry-After` | Seconds to wait; **only on 429**. |

`Retry-After` ("when can I try again?") vs `X-RateLimit-Reset` ("when does my full quota return?")
is the classic probe — they coincide for a fixed window but are different concepts.

---

## 6. Tests (WebApplicationFactory + Testcontainers Redis)

Each test spins the app up with a unique `RateLimit:KeyNamespace` and its own limits (via
`UseSetting`), so tests are isolated on shared Redis and fully deterministic.

| Test | Asserts |
|---|---|
| `Headers_present_on_successful_requests` | All `X-RateLimit-*` headers on a 200. |
| `Returns_429_on_first_violated_dimension_with_correct_headers` | 429 + `Retry-After` + `dimension=anon` in header and body. |
| `Applies_all_three_limiters_in_order` | Auth+key ⇒ ip→user→apikey; the tight `user` dim is reported (fail-fast, in order). |
| `Authenticated_user_gets_higher_limit_than_anonymous` | anon blocked at 3, user allowed 8. |
| `Whitelisted_service_token_bypasses_limiting` | 20 requests vs limit 2, all allowed. |

---

## 7. Run it

```bash
docker compose up -d                                             # Redis
dotnet test tests/Level5.Tests                                   # 5 tests (own Redis container)
dotnet run --project src/Level5.Middleware --urls http://localhost:5095
```

Open **http://localhost:5095/** — pick an identity (anonymous / authenticated / api key / internal
token) and fire requests; the log shows which **dimension** each response hit. Config:
`RateLimit:{Ip,Anon,User,ApiKey}:Limit`, `RateLimit:WindowSeconds`, `RateLimit:WhitelistToken`.

---

## Interview takeaway (say it cleanly)

> Production limiting is multi-dimensional: cap per IP, per user, and per API key simultaneously with
> a chain of policies, and reject on the first violated one. Order cheapest / most-likely-to-fail
> first, because fail-fast still charges the dimensions you evaluated before the failure. A null
> key-selector cleanly skips a dimension, which lets one chain serve anonymous (tight, per-IP),
> authenticated (higher, per-user), and api-key traffic. Know the header set cold — especially
> `Retry-After` vs `X-RateLimit-Reset`.
