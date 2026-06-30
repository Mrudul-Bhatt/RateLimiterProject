using Level1.FixedWindow;
using RateLimiting.Abstractions;

var builder = WebApplication.CreateBuilder(args);

// --- Rate limiter wiring ---------------------------------------------------
// Limit/window are read from configuration so they can be tuned without a rebuild,
// with small defaults that make the boundary burst easy to observe by hand.
var limit = builder.Configuration.GetValue("RateLimit:Limit", 5L);
var windowSeconds = builder.Configuration.GetValue("RateLimit:WindowSeconds", 10);
var options = new FixedWindowOptions(limit, TimeSpan.FromSeconds(windowSeconds));

// TimeProvider.System is the real clock in production; tests inject a FakeTimeProvider.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IRateLimiter, FixedWindowRateLimiter>();

var app = builder.Build();

// Serve the live dashboard (wwwroot/index.html) BEFORE the limiter so loading the page and its
// assets never consumes quota. UseDefaultFiles maps "/" -> index.html.
app.UseDefaultFiles();
app.UseStaticFiles();

// The limiter runs as middleware so it protects every endpoint placed after it.
app.UseMiddleware<RateLimitingMiddleware>();

// The single protected endpoint from the roadmap.
app.MapGet("/api/resource", () => Results.Ok(new
{
    data = "you got the resource",
    servedAt = DateTimeOffset.UtcNow
}));

// --- Debug/observability endpoint -----------------------------------------
// Exposes the limiter's live internal state so the dashboard can visualise it. The middleware
// exempts "/debug/*" from limiting so polling this doesn't perturb what it's measuring.
app.MapGet("/debug/state", (IRateLimiter limiter, TimeProvider clock) =>
{
    if (limiter is not FixedWindowRateLimiter fixedWindow)
        return Results.Problem("State inspection is only available for FixedWindowRateLimiter.");

    return Results.Ok(new
    {
        limit = fixedWindow.Limit,
        windowSeconds = fixedWindow.Window.TotalSeconds,
        now = clock.GetUtcNow(),
        keys = fixedWindow.GetState()
    });
});

app.Run();

// Exposed so WebApplicationFactory<Program> can spin the app up in integration tests.
public partial class Program;
