using System.Diagnostics;
using Prometheus;

namespace Level7.Gateway;

/// <summary>
/// The observability surface for the gateway: the four "golden" limiter metrics (Prometheus) plus a
/// tracing <see cref="System.Diagnostics.ActivitySource"/>. Metric names match the roadmap so the
/// Grafana dashboard and alert rules bind to them.
/// </summary>
public static class GatewayTelemetry
{
    public const string SourceName = "RateLimiter.Gateway";
    public static readonly ActivitySource ActivitySource = new(SourceName);

    // requests seen, by endpoint / result (allowed|blocked) / source (redis|local_fallback|fail_open|fail_closed)
    public static readonly Counter RequestsTotal = Metrics.CreateCounter(
        "rate_limit_requests_total", "Rate-limit decisions.", new CounterConfiguration
        {
            LabelNames = ["endpoint", "result", "source"]
        });

    // rejections, by endpoint / reason (over_limit|fail_closed)
    public static readonly Counter RejectedTotal = Metrics.CreateCounter(
        "rate_limit_rejected_total", "Rejected requests.", new CounterConfiguration
        {
            LabelNames = ["endpoint", "reason"]
        });

    // latency of the Redis limiter round trip
    public static readonly Histogram RedisCallDuration = Metrics.CreateHistogram(
        "redis_call_duration_seconds", "Redis limiter call latency.", new HistogramConfiguration
        {
            Buckets = Histogram.ExponentialBuckets(0.0005, 2, 12) // ~0.5ms .. ~1s
        });

    // 1 while the Redis circuit breaker is open (degraded), 0 when healthy — for alerting.
    public static readonly Gauge CircuitOpen = Metrics.CreateGauge(
        "rate_limit_circuit_open", "1 when the Redis circuit breaker is open (degraded).");
}
