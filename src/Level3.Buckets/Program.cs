using Level3.Buckets;

var builder = WebApplication.CreateBuilder(args);

// Two limiters side by side, deliberately given the SAME capacity + rate so the only thing that
// differs is the algorithm. Defaults chosen to make the burst-vs-smooth contrast easy to see by hand.
var capacity = builder.Configuration.GetValue("RateLimit:Capacity", 5);
var rate = builder.Configuration.GetValue("RateLimit:RatePerSecond", 2.0);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new TokenBucketRateLimiter(
    new TokenBucketOptions(capacity, rate), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(sp => new LeakyBucketRateLimiter(
    new LeakyBucketOptions(capacity, rate), sp.GetRequiredService<TimeProvider>()));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// Each algorithm gets its own endpoint so the dashboard can hit them independently.
app.MapGet("/api/token", (HttpContext ctx, TokenBucketRateLimiter limiter) =>
    Decide(ctx, limiter.CheckAsync(ClientKey(ctx))));

app.MapGet("/api/leaky", (HttpContext ctx, LeakyBucketRateLimiter limiter) =>
    Decide(ctx, limiter.CheckAsync(ClientKey(ctx))));

// Live state of both buckets for the dashboard.
app.MapGet("/debug/state", (TokenBucketRateLimiter token, LeakyBucketRateLimiter leaky, TimeProvider clock) =>
    Results.Ok(new
    {
        now = clock.GetUtcNow(),
        token = new { capacity = token.Capacity, refillPerSecond = token.RefillPerSecond, keys = token.GetState() },
        leaky = new { capacity = leaky.Capacity, leakPerSecond = leaky.LeakPerSecond, keys = leaky.GetState() }
    }));

app.Run();

// Emit standard headers, and a 429 body when blocked. Shared by both endpoints.
static async Task Decide(HttpContext ctx, Task<RateLimiting.Abstractions.RateLimitResult> pending)
{
    var result = await pending;
    var h = ctx.Response.Headers;
    h["X-RateLimit-Limit"] = result.Limit.ToString();
    h["X-RateLimit-Remaining"] = Math.Max(0, result.Remaining).ToString();

    if (result.Allowed)
    {
        await ctx.Response.WriteAsJsonAsync(new { ok = true, remaining = result.Remaining });
        return;
    }

    var retryAfterSeconds = Math.Ceiling((result.RetryAfter ?? TimeSpan.Zero).TotalSeconds);
    ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
    h["Retry-After"] = ((int)retryAfterSeconds).ToString();
    await ctx.Response.WriteAsJsonAsync(new { ok = false, error = "rate_limit_exceeded", retryAfterSeconds });
}

static string ClientKey(HttpContext ctx)
{
    var ip = ctx.Connection.RemoteIpAddress?.ToString();
    return string.IsNullOrEmpty(ip) ? "unknown" : $"ip:{ip}";
}

public partial class Program;
