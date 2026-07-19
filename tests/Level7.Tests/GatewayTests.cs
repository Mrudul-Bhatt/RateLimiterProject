using System.Diagnostics;
using System.Net;
using System.Collections.Concurrent;
using Level7.Gateway;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RateLimiting.Abstractions;
using Xunit;

namespace Level7.Tests;

/// <summary>
/// A controllable stand-in for the Redis limiter so we can simulate: normal allow, normal block, and
/// a Redis OUTAGE (throws). No containers needed — Level 7's new logic is resilience/metrics/tracing,
/// all deterministically testable with this stub.
/// </summary>
internal sealed class StubLimiter : IRateLimiter
{
    public enum M { Allow, Block, Throw }
    public volatile M Mode = M.Allow;

    public Task<RateLimitResult> CheckAsync(string key, CancellationToken ct = default) => Mode switch
    {
        M.Allow => Task.FromResult(RateLimitResult.Allow(100, 99, DateTimeOffset.UtcNow.AddSeconds(10))),
        M.Block => Task.FromResult(RateLimitResult.Block(100, DateTimeOffset.UtcNow.AddSeconds(10), TimeSpan.FromSeconds(10))),
        _ => throw new InvalidOperationException("simulated Redis outage")
    };
}

public class GatewayTests
{
    private static WebApplicationFactory<Program> App(StubLimiter stub, params (string, string)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            foreach (var (k, v) in settings) b.UseSetting(k, v);
            // Swap the "primary" (Redis) limiter for our stub.
            b.ConfigureServices(s => s.AddKeyedSingleton<IRateLimiter>("primary", stub));
        });

    [Fact]
    public async Task Gateway_enforces_limit_before_forwarding()
    {
        var stub = new StubLimiter { Mode = StubLimiter.M.Block };
        using var app = App(stub);
        var client = app.CreateClient();

        var res = await client.GetAsync("/api/thing");
        var body = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        Assert.DoesNotContain("reached", body);   // backend echo was never invoked
        Assert.Equal("redis", res.Headers.GetValues("X-RateLimit-Source").Single());
    }

    [Fact]
    public async Task Allowed_request_is_forwarded_to_backend()
    {
        var stub = new StubLimiter { Mode = StubLimiter.M.Allow };
        using var app = App(stub);
        var client = app.CreateClient();

        var res = await client.GetAsync("/api/thing");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("reached", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Metrics_increment_on_allow_and_reject()
    {
        var stub = new StubLimiter();
        using var app = App(stub);
        var client = app.CreateClient();

        stub.Mode = StubLimiter.M.Allow;
        await client.GetAsync("/api/thing");
        stub.Mode = StubLimiter.M.Block;
        await client.GetAsync("/api/thing");

        var metrics = await client.GetStringAsync("/metrics");
        Assert.Contains("rate_limit_requests_total", metrics);
        Assert.Contains("result=\"allowed\"", metrics);
        Assert.Contains("rate_limit_rejected_total", metrics);
        Assert.Contains("reason=\"over_limit\"", metrics);
    }

    [Fact]
    public async Task Trace_contains_limiter_decision_attributes()
    {
        var activities = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == GatewayTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(listener);

        var stub = new StubLimiter { Mode = StubLimiter.M.Allow };
        using var app = App(stub);
        await app.CreateClient().GetAsync("/api/thing");

        var span = Assert.Single(activities, a => a.OperationName == "rate_limit.check");
        Assert.Equal("allowed", span.GetTagItem("rate_limit.result"));
        Assert.Equal("redis", span.GetTagItem("rate_limit.source"));
        Assert.NotNull(span.GetTagItem("rate_limit.key"));
    }

    [Fact]
    public async Task Redis_down_fails_open_when_configured()
    {
        var stub = new StubLimiter { Mode = StubLimiter.M.Throw };
        using var app = App(stub, ("Gateway:DegradeMode", "FailOpen"));
        var res = await app.CreateClient().GetAsync("/api/thing");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);   // allowed despite Redis being down
        Assert.Equal("fail_open", res.Headers.GetValues("X-RateLimit-Source").Single());
    }

    [Fact]
    public async Task Redis_down_fails_closed_when_configured()
    {
        var stub = new StubLimiter { Mode = StubLimiter.M.Throw };
        using var app = App(stub, ("Gateway:DegradeMode", "FailClosed"));
        var res = await app.CreateClient().GetAsync("/api/thing");

        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        Assert.Equal("fail_closed", res.Headers.GetValues("X-RateLimit-Source").Single());
    }

    [Fact]
    public async Task Local_fallback_limiter_engages_on_redis_outage()
    {
        // Redis is down; degrade to the in-process Level-1 limiter with a limit of 2.
        var stub = new StubLimiter { Mode = StubLimiter.M.Throw };
        using var app = App(stub, ("Gateway:DegradeMode", "LocalFallback"), ("Gateway:Fallback:Limit", "2"));
        var client = app.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
            statuses.Add((await client.GetAsync("/api/thing")).StatusCode);

        // First two allowed by the local limiter, third blocked — a per-instance cap still holds.
        Assert.Equal(HttpStatusCode.OK, statuses[0]);
        Assert.Equal(HttpStatusCode.OK, statuses[1]);
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[2]);

        var last = await client.GetAsync("/api/thing");
        Assert.Equal("local_fallback", last.Headers.GetValues("X-RateLimit-Source").Single());
    }
}
