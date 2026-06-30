using Level1.FixedWindow;
using Level2.SlidingWindowLog;
using RateLimiting.Abstractions;

// Memory footprint: fixed-window counter (O(1) per key) vs sliding-window log (O(requests in
// window) per key). To expose the growth we use a HUGE limit and a LONG window so nothing is
// rejected or evicted — every request is retained. (In production the per-key cost is capped at
// `limit`, but that cap can itself be large, which is the whole point.)

const int keys = 1_000;
var requestCounts = new[] { 10, 100, 1_000 };
var window = TimeSpan.FromHours(1);
const long hugeLimit = long.MaxValue;

Console.WriteLine($"Keys: {keys:N0} | window: {window} | limit: (effectively unbounded)\n");
Console.WriteLine($"{"reqs/key",-10}{"total reqs",-14}{"FixedWindow",-16}{"SlidingLog",-16}{"ratio",-8}");
Console.WriteLine(new string('-', 64));

foreach (var reqsPerKey in requestCounts)
{
    var fixedBytes = Measure(() => new FixedWindowRateLimiter(
        new FixedWindowOptions(hugeLimit, window), TimeProvider.System), keys, reqsPerKey);

    var slidingBytes = Measure(() => new SlidingWindowLogRateLimiter(
        new SlidingWindowLogOptions(hugeLimit, window), TimeProvider.System), keys, reqsPerKey);

    var ratio = slidingBytes / (double)Math.Max(1, fixedBytes);
    Console.WriteLine($"{reqsPerKey,-10:N0}{(keys * (long)reqsPerKey),-14:N0}{Fmt(fixedBytes),-16}{Fmt(slidingBytes),-16}{ratio,-8:N1}x");
}

Console.WriteLine("\nFixedWindow stays flat (one counter per key). SlidingLog grows linearly with");
Console.WriteLine("requests-in-window — that is the accuracy/memory trade-off Level 2 buys.");

static long Measure(Func<IRateLimiter> factory, int keys, int reqsPerKey)
{
    var limiter = factory();

    // Settle, then take a clean baseline before loading state.
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    var before = GC.GetTotalMemory(forceFullCollection: true);

    for (var k = 0; k < keys; k++)
    {
        var key = $"key-{k}";
        for (var r = 0; r < reqsPerKey; r++)
            limiter.CheckAsync(key).GetAwaiter().GetResult();
    }

    // forceFullCollection drops the transient Task/closure garbage, leaving only retained state.
    var after = GC.GetTotalMemory(forceFullCollection: true);
    GC.KeepAlive(limiter);
    return after - before;
}

static string Fmt(long bytes) =>
    bytes >= 1_048_576 ? $"{bytes / 1_048_576.0:N1} MB" :
    bytes >= 1_024 ? $"{bytes / 1_024.0:N1} KB" :
    $"{bytes} B";
