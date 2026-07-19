using System.Diagnostics;
using Polly;
using Polly.CircuitBreaker;
using RateLimiting.Abstractions;

namespace Level7.Gateway;

/// <summary>What to do when the Redis limiter is unavailable. This is a BUSINESS RISK choice.</summary>
public enum DegradeMode
{
    /// <summary>Allow everything. Prioritises availability; a Redis outage can let abuse through.</summary>
    FailOpen,
    /// <summary>Reject everything. Prioritises protection; a Redis outage rejects legitimate traffic.</summary>
    FailClosed,
    /// <summary>Fall back to a per-process in-memory limiter — degrade gracefully instead of going fully open.</summary>
    LocalFallback
}

/// <summary>The gateway's decision plus how it was reached (for metrics/tracing).</summary>
public sealed record GatewayDecision(RateLimitResult Result, string Source, bool Degraded);

/// <summary>
/// Wraps the primary (Redis) limiter in a Polly v8 circuit breaker. While Redis is healthy, calls go
/// straight through and we record their latency. When Redis errors — or the breaker has tripped open
/// after repeated failures — we stop hammering it and DEGRADE according to the configured
/// <see cref="DegradeMode"/>, logging loudly. This is the crux of Level 7: a defensible, configurable
/// answer to "what happens when Redis is down?"
/// </summary>
public sealed class ResilientRateLimiter
{
    private readonly IRateLimiter _redis;
    private readonly IRateLimiter _localFallback;
    private readonly DegradeMode _mode;
    private readonly ILogger<ResilientRateLimiter> _logger;
    private readonly ResiliencePipeline<RateLimitResult> _pipeline;

    public ResilientRateLimiter(
        IRateLimiter redis, IRateLimiter localFallback, DegradeMode mode, ILogger<ResilientRateLimiter> logger)
    {
        _redis = redis;
        _localFallback = localFallback;
        _mode = mode;
        _logger = logger;

        _pipeline = new ResiliencePipelineBuilder<RateLimitResult>()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<RateLimitResult>
            {
                // Trip open once half of a small sample of calls fail, and stop calling Redis for a
                // short cool-off — so a Redis outage doesn't add a timeout to every request.
                FailureRatio = 0.5,
                MinimumThroughput = 3,
                SamplingDuration = TimeSpan.FromSeconds(10),
                BreakDuration = TimeSpan.FromSeconds(5),
                ShouldHandle = new PredicateBuilder<RateLimitResult>().Handle<Exception>(),
                OnOpened = _ => { GatewayTelemetry.CircuitOpen.Set(1); return default; },
                OnClosed = _ => { GatewayTelemetry.CircuitOpen.Set(0); return default; }
            })
            .Build();
    }

    public DegradeMode Mode => _mode;

    public async Task<GatewayDecision> EvaluateAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var sw = Stopwatch.GetTimestamp();
            var result = await _pipeline.ExecuteAsync(async token => await _redis.CheckAsync(key, token), ct);
            GatewayTelemetry.RedisCallDuration.Observe(Stopwatch.GetElapsedTime(sw).TotalSeconds);
            return new GatewayDecision(result, Source: "redis", Degraded: false);
        }
        catch (Exception ex) when (ex is BrokenCircuitException or not OperationCanceledException)
        {
            return await DegradeAsync(key, ex, ct);
        }
    }

    private async Task<GatewayDecision> DegradeAsync(string key, Exception cause, CancellationToken ct)
    {
        // Loud, so a degraded gateway is obvious in logs and dashboards — never silent.
        _logger.LogWarning(cause,
            "Redis limiter unavailable — DEGRADING via {Mode} for key {Key} ({Cause})",
            _mode, key, cause.GetType().Name);

        switch (_mode)
        {
            case DegradeMode.FailOpen:
                // Allow, with sentinel headers (-1) so clients/dashboards can tell it was degraded.
                return new GatewayDecision(
                    RateLimitResult.Allow(-1, -1, DateTimeOffset.MinValue), Source: "fail_open", Degraded: true);

            case DegradeMode.FailClosed:
                return new GatewayDecision(
                    RateLimitResult.Block(-1, DateTimeOffset.MinValue, TimeSpan.FromSeconds(5)), Source: "fail_closed", Degraded: true);

            case DegradeMode.LocalFallback:
            default:
                var local = await _localFallback.CheckAsync(key, ct);
                return new GatewayDecision(local, Source: "local_fallback", Degraded: true);
        }
    }
}
