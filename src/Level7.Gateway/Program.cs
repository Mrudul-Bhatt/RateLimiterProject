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

builder.Services.AddSingleton(sp => new ResilientRateLimiter(
    sp.GetRequiredKeyedService<IRateLimiter>("primary"),
    sp.GetRequiredKeyedService<IRateLimiter>("fallback"),
    degradeMode,
    sp.GetRequiredService<ILogger<ResilientRateLimiter>>()));

// YARP reverse proxy (routes/clusters come from the "ReverseProxy" config section, if present).
builder.Services.AddReverseProxy().LoadFromConfig(cfg.GetSection("ReverseProxy"));

var app = builder.Build();

// Edge enforcement runs before everything downstream (proxy included).
app.UseMiddleware<GatewayRateLimitingMiddleware>();

app.MapMetrics();          // Prometheus scrape endpoint at /metrics (exempted in the middleware)
app.MapGet("/debug/state", () => Results.Ok(new
{
    limit, windowSeconds, degradeMode = degradeMode.ToString(), fallbackLimit,
    redisConnected = multiplexer.IsConnected
}));

app.MapReverseProxy();     // forwards configured routes to the backend cluster
// If no proxy route matched (e.g. tests, or no ReverseProxy config), this stands in for "the backend".
app.MapFallback(() => Results.Ok(new { backend = "reached (echo)", note = "configure ReverseProxy to forward to Level7.Backend" }));

app.Run();

public partial class Program;
