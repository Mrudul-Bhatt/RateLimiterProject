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

// The limiter runs as middleware so it protects every endpoint placed after it.
app.UseMiddleware<RateLimitingMiddleware>();

// The single protected endpoint from the roadmap.
app.MapGet("/api/resource", () => Results.Ok(new
{
    data = "you got the resource",
    servedAt = DateTimeOffset.UtcNow
}));

app.MapGet("/", () => "Level 1 — Fixed Window Rate Limiter. Try GET /api/resource");

app.Run();

// Exposed so WebApplicationFactory<Program> can spin the app up in integration tests.
public partial class Program;
