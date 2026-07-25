namespace Level7.Gateway;

/// <summary>
/// A mutable holder for the active <see cref="DegradeMode"/>, so the dashboard's /debug endpoints
/// can flip it live (fail-open vs fail-closed vs local-fallback) without restarting the process.
/// Registered as a singleton; starts at whatever "Gateway:DegradeMode" was configured.
/// </summary>
public sealed class DegradeModeSwitch(DegradeMode initial)
{
    private DegradeMode _mode = initial;
    public DegradeMode Mode { get => _mode; set => _mode = value; }
}

/// <summary>
/// A manual "pretend Redis is down" switch for demos. When enabled, <see cref="ResilientRateLimiter"/>
/// throws before calling the real Redis limiter — which trips the SAME Polly circuit breaker a real
/// outage would, so the dashboard can show fail-open / fail-closed / local-fallback live without
/// needing `docker stop` on the Redis container. Off by default; never touched by production traffic
/// logic other than this one check.
/// </summary>
public sealed class OutageSimulator
{
    public volatile bool Enabled;
}
