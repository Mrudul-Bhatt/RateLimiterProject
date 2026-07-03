using Level2.SlidingWindowLog;
using RateLimiting.Abstractions;

var builder = WebApplication.CreateBuilder(args);

// Console logs with a millisecond timestamp on every line, so you can see WHEN each decision
// happened (and watch the trailing window slide as timestamps age out).
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.TimestampFormat = "HH:mm:ss.fff ";
    o.UseUtcTimestamp = true;
    o.SingleLine = true;
});

// --- Rate limiter wiring ---------------------------------------------------
var limit = builder.Configuration.GetValue("RateLimit:Limit", 5L);
var windowSeconds = builder.Configuration.GetValue("RateLimit:WindowSeconds", 10);
var options = new SlidingWindowLogOptions(limit, TimeSpan.FromSeconds(windowSeconds));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(options);
// Factory registration so we can pass the limiter a named logger (it logs each ALLOW/BLOCK).
builder.Services.AddSingleton<IRateLimiter>(sp => new SlidingWindowLogRateLimiter(
    sp.GetRequiredService<SlidingWindowLogOptions>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("SlidingWindow")));

// The cleanup sweep that fixed window never needed. Short interval here so it's easy to observe.
builder.Services.AddHostedService(sp => new SlidingWindowLogCleanupService(
    sp.GetRequiredService<IRateLimiter>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<SlidingWindowLogCleanupService>>(),
    interval: TimeSpan.FromSeconds(15)));

var app = builder.Build();

// Dashboard served before the limiter so loading it never consumes quota.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseMiddleware<RateLimitingMiddleware>();

app.MapGet("/api/resource", () => Results.Ok(new
{
    data = "you got the resource",
    servedAt = DateTimeOffset.UtcNow
}));

// Live internal state for the dashboard. Exempt from limiting (see middleware).
app.MapGet("/debug/state", (IRateLimiter limiter, TimeProvider clock) =>
{
    if (limiter is not SlidingWindowLogRateLimiter slidingLog)
        return Results.Problem("State inspection is only available for SlidingWindowLogRateLimiter.");

    return Results.Ok(new
    {
        limit = slidingLog.Limit,
        windowSeconds = slidingLog.Window.TotalSeconds,
        trackedKeys = slidingLog.TrackedKeyCount,
        now = clock.GetUtcNow(),
        keys = slidingLog.GetState()
    });
});

app.Run();

public partial class Program;
