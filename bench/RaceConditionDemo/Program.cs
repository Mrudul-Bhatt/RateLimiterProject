using System.Diagnostics;
using Level1.FixedWindow;
using Level4.RedisAtomic;
using RateLimiting.Abstractions;
using StackExchange.Redis;

// Demonstrates WHY Level 4 exists:
//   1. Two in-memory limiters (separate "instances") let a client exceed the global limit.
//   2. Two Redis-backed limiters sharing one Redis hold the limit exactly.
// Then measures the latency cost of the Redis round trip vs in-process.
//
// Needs Redis on localhost:6379:  docker compose up -d

var redisConn = Environment.GetEnvironmentVariable("REDIS") ?? "localhost:6379";
IConnectionMultiplexer redis;
try
{
    redis = await ConnectionMultiplexer.ConnectAsync(redisConn);
}
catch (Exception ex)
{
    Console.WriteLine($"Could not connect to Redis at {redisConn}. Start it with `docker compose up -d`.\n{ex.Message}");
    return;
}

const long limit = 10;
var window = TimeSpan.FromSeconds(30);

Console.WriteLine($"Rate limit: {limit} requests / {window.TotalSeconds:N0}s per client");
Console.WriteLine("A load balancer sprays ONE client's 30 requests across TWO app instances.\n");

// --- 1. In-memory: each instance has its own counter -----------------------------------------
var memA = new FixedWindowRateLimiter(new FixedWindowOptions(limit, window), TimeProvider.System);
var memB = new FixedWindowRateLimiter(new FixedWindowOptions(limit, window), TimeProvider.System);
var memAllowed = await SprayAcrossTwo(memA, memB, "client-mem");

// --- 2. Redis: both instances share one counter ----------------------------------------------
var prefix = $"demo:{Guid.NewGuid():N}:";
var redisA = new RedisFixedWindowRateLimiter(redis, new RedisFixedWindowOptions(limit, window, prefix), TimeProvider.System);
var redisB = new RedisFixedWindowRateLimiter(redis, new RedisFixedWindowOptions(limit, window, prefix), TimeProvider.System);
var redisAllowed = await SprayAcrossTwo(redisA, redisB, "client-redis");

Console.WriteLine($"  IN-MEMORY (2 instances):  {memAllowed} allowed  ->  {(memAllowed > limit ? $"LIMIT VIOLATED (got {memAllowed}, wanted {limit})" : "ok")}");
Console.WriteLine($"  REDIS     (2 instances):  {redisAllowed} allowed  ->  {(redisAllowed == limit ? "held exactly at the limit" : "unexpected")}");

// --- 3. Latency: the price of correctness ----------------------------------------------------
Console.WriteLine("\nLatency (avg per CheckAsync, warm):");
var memLatency = await MeasureLatency(
    new FixedWindowRateLimiter(new FixedWindowOptions(long.MaxValue, window), TimeProvider.System), "lat-mem");
var redisLatency = await MeasureLatency(
    new RedisFixedWindowRateLimiter(redis, new RedisFixedWindowOptions(long.MaxValue, window, prefix), TimeProvider.System), "lat-redis");
Console.WriteLine($"  in-process : {memLatency * 1000:N1} us");
Console.WriteLine($"  redis      : {redisLatency:N3} ms   (~{redisLatency / Math.Max(memLatency, 0.0001):N0}x slower, the cost of a shared, correct counter)");

await redis.DisposeAsync();
return;

static async Task<int> SprayAcrossTwo(IRateLimiter a, IRateLimiter b, string key)
{
    var allowed = 0;
    for (var i = 0; i < 30; i++)
    {
        var limiter = i % 2 == 0 ? a : b;
        if ((await limiter.CheckAsync(key)).Allowed) allowed++;
    }
    return allowed;
}

static async Task<double> MeasureLatency(IRateLimiter limiter, string key)
{
    for (var i = 0; i < 50; i++) await limiter.CheckAsync(key); // warm up

    const int n = 1000;
    var sw = Stopwatch.StartNew();
    for (var i = 0; i < n; i++) await limiter.CheckAsync(key);
    sw.Stop();
    return sw.Elapsed.TotalMilliseconds / n;
}
