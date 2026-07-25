using Level4.RedisAtomic;
using Level7.Gateway;
using OpenTelemetry.Trace;
using Prometheus;
using RateLimiting.Abstractions;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.TimestampFormat = "HH:mm:ss.fff ";
    o.UseUtcTimestamp = true;
    o.SingleLine = true;
});

var cfg = builder.Configuration;
var redisConn = cfg.GetValue("Redis:ConnectionString", "localhost:6379")!;
var limit = cfg.GetValue("Gateway:Limit", 100L);
var windowSeconds = cfg.GetValue("Gateway:WindowSeconds", 10);
var window = TimeSpan.FromSeconds(windowSeconds);
var degradeMode = Enum.Parse<DegradeMode>(cfg.GetValue("Gateway:DegradeMode", nameof(DegradeMode.LocalFallback))!, ignoreCase: true);
var fallbackLimit = cfg.GetValue("Gateway:Fallback:Limit", 1_000L);

var redisConfig = ConfigurationOptions.Parse(redisConn);
redisConfig.AbortOnConnectFail = false;   // the app must start even if Redis is down — that's the point
var multiplexer = await ConnectionMultiplexer.ConnectAsync(redisConfig);

// Distributed tracing: emit spans from our ActivitySource + ASP.NET Core, to the console for the demo.
builder.Services.AddOpenTelemetry().WithTracing(t => t
    .AddSource(GatewayTelemetry.SourceName)
    .AddAspNetCoreInstrumentation()
    .AddConsoleExporter());

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IConnectionMultiplexer>(multiplexer);

// Primary limiter = Redis (Level 4). Keyed so tests can swap it for a stub that simulates an outage.
builder.Services.AddKeyedSingleton<IRateLimiter>("primary", (sp, _) => new RedisSlidingWindowRateLimiter(
    sp.GetRequiredService<IConnectionMultiplexer>(),
    new RedisSlidingWindowOptions(limit, window, "rl:gateway:"),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("Gateway.Redis")));

// Fallback limiter = the in-process Level 1 algorithm (degraded mode).
builder.Services.AddKeyedSingleton<IRateLimiter>("fallback", (sp, _) =>
    new InProcessFallbackLimiter(fallbackLimit, window, sp.GetRequiredService<TimeProvider>()));

// Mutable at runtime via /debug endpoints, so the dashboard can flip degrade mode / simulate a Redis
// outage live, without a restart or touching the Docker container.
builder.Services.AddSingleton(new DegradeModeSwitch(degradeMode));
builder.Services.AddSingleton<OutageSimulator>();

builder.Services.AddSingleton(sp => new ResilientRateLimiter(
    sp.GetRequiredKeyedService<IRateLimiter>("primary"),
    sp.GetRequiredKeyedService<IRateLimiter>("fallback"),
    sp.GetRequiredService<DegradeModeSwitch>(),
    sp.GetRequiredService<OutageSimulator>(),
    sp.GetRequiredService<ILogger<ResilientRateLimiter>>()));

// YARP reverse proxy (routes/clusters come from the "ReverseProxy" config section, if present).
builder.Services.AddReverseProxy().LoadFromConfig(cfg.GetSection("ReverseProxy"));

var app = builder.Build();

// Dashboard served before the limiter so loading it never counts against your quota.
app.UseDefaultFiles();
app.UseStaticFiles();

// Edge enforcement runs before everything downstream (proxy included).
app.UseMiddleware<GatewayRateLimitingMiddleware>();

app.MapMetrics();          // Prometheus scrape endpoint at /metrics (exempted in the middleware)

app.MapGet("/debug/state", (DegradeModeSwitch modeSwitch, OutageSimulator outage) => Results.Ok(new
{
    limit, windowSeconds, fallbackLimit,
    degradeMode = modeSwitch.Mode.ToString(),
    simulatedOutage = outage.Enabled,
    circuitOpen = GatewayTelemetry.CircuitOpen.Value == 1,
    redisConnected = multiplexer.IsConnected
}));

// --- Demo controls: flip these live from the dashboard --------------------
app.MapPost("/debug/degrade-mode", (DegradeModeRequest req, DegradeModeSwitch modeSwitch) =>
{
    if (!Enum.TryParse<DegradeMode>(req.Mode, ignoreCase: true, out var parsed))
        return Results.BadRequest(new { error = "invalid_mode", valid = Enum.GetNames<DegradeMode>() });
    modeSwitch.Mode = parsed;
    return Results.Ok(new { degradeMode = modeSwitch.Mode.ToString() });
});

app.MapPost("/debug/simulate-outage", (OutageRequest req, OutageSimulator outage) =>
{
    outage.Enabled = req.Enabled;
    return Results.Ok(new { simulatedOutage = outage.Enabled });
});

app.MapReverseProxy();     // forwards configured routes to the backend cluster
// If no proxy route matched (e.g. tests, or no ReverseProxy config), this stands in for "the backend".
app.MapFallback(() => Results.Ok(new { backend = "reached (echo)", note = "configure ReverseProxy to forward to Level7.Backend" }));

app.Run();

public record DegradeModeRequest(string Mode);
public record OutageRequest(bool Enabled);

public partial class Program;
