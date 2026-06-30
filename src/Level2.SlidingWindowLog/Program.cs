using Level2.SlidingWindowLog;
using RateLimiting.Abstractions;

var builder = WebApplication.CreateBuilder(args);

// --- Rate limiter wiring ---------------------------------------------------
var limit = builder.Configuration.GetValue("RateLimit:Limit", 5L);
var windowSeconds = builder.Configuration.GetValue("RateLimit:WindowSeconds", 10);
var options = new SlidingWindowLogOptions(limit, TimeSpan.FromSeconds(windowSeconds));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IRateLimiter, SlidingWindowLogRateLimiter>();

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
