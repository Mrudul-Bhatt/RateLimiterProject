using Level4.RedisAtomic;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Timestamped, single-line console logs (same convention as Levels 1–3).
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.TimestampFormat = "HH:mm:ss.fff ";
    o.UseUtcTimestamp = true;
    o.SingleLine = true;
});

var redisConn = builder.Configuration.GetValue("Redis:ConnectionString", "localhost:6379")!;
var limit = builder.Configuration.GetValue("RateLimit:Limit", 5L);
var windowSeconds = builder.Configuration.GetValue("RateLimit:WindowSeconds", 10);
var window = TimeSpan.FromSeconds(windowSeconds);

// AbortOnConnectFail=false so the app still starts if Redis is briefly unavailable (it retries).
var redisConfig = ConfigurationOptions.Parse(redisConn);
redisConfig.AbortOnConnectFail = false;
var multiplexer = await ConnectionMultiplexer.ConnectAsync(redisConfig);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IConnectionMultiplexer>(multiplexer);
builder.Services.AddSingleton(sp => new RedisFixedWindowRateLimiter(
    sp.GetRequiredService<IConnectionMultiplexer>(),
    new RedisFixedWindowOptions(limit, window),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("RedisFixed")));
builder.Services.AddSingleton(sp => new RedisSlidingWindowRateLimiter(
    sp.GetRequiredService<IConnectionMultiplexer>(),
    new RedisSlidingWindowOptions(limit, window),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("RedisSliding")));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var clientKey = (HttpContext ctx) =>
{
    var ip = ctx.Connection.RemoteIpAddress?.ToString();
    return string.IsNullOrEmpty(ip) ? "unknown" : $"ip:{ip}";
};

app.MapGet("/api/fixed", (HttpContext ctx, RedisFixedWindowRateLimiter limiter) =>
    Decide(ctx, limiter.CheckAsync(clientKey(ctx))));
app.MapGet("/api/sliding", (HttpContext ctx, RedisSlidingWindowRateLimiter limiter) =>
    Decide(ctx, limiter.CheckAsync(clientKey(ctx))));

// Live state straight from Redis (this is the shared source of truth all instances see).
app.MapGet("/debug/state", async (IConnectionMultiplexer redis) =>
{
    var db = redis.GetDatabase();
    var server = redis.GetServer(redis.GetEndPoints()[0]);

    async Task<object[]> Snapshot(string prefix, bool sortedSet)
    {
        var keys = new List<object>();
        foreach (var k in server.Keys(pattern: prefix + "*", pageSize: 100))
        {
            var count = sortedSet ? await db.SortedSetLengthAsync(k) : (long)await db.StringGetAsync(k);
            var ttl = await db.KeyTimeToLiveAsync(k);
            keys.Add(new { key = k.ToString(), count, ttlSeconds = ttl?.TotalSeconds });
        }
        return [.. keys];
    }

    return Results.Ok(new
    {
        limit,
        windowSeconds,
        connected = redis.IsConnected,
        fixedKeys = await Snapshot("rl:fixed:", sortedSet: false),
        slidingKeys = await Snapshot("rl:sliding:", sortedSet: true)
    });
});

app.Run();

static async Task Decide(HttpContext ctx, Task<RateLimiting.Abstractions.RateLimitResult> pending)
{
    var result = await pending;
    var h = ctx.Response.Headers;
    h["X-RateLimit-Limit"] = result.Limit.ToString();
    h["X-RateLimit-Remaining"] = Math.Max(0, result.Remaining).ToString();
    h["X-RateLimit-Reset"] = result.ResetsAt.ToUnixTimeSeconds().ToString();

    if (result.Allowed)
    {
        await ctx.Response.WriteAsJsonAsync(new { ok = true, remaining = result.Remaining });
        return;
    }

    var retryAfterSeconds = (int)Math.Ceiling((result.RetryAfter ?? TimeSpan.Zero).TotalSeconds);
    ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
    h["Retry-After"] = retryAfterSeconds.ToString();
    await ctx.Response.WriteAsJsonAsync(new { ok = false, error = "rate_limit_exceeded", retryAfterSeconds });
}

public partial class Program;
