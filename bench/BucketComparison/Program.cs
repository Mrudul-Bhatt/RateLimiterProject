using Level3.Buckets;
using Microsoft.Extensions.Time.Testing;

// Side-by-side comparison: feed the SAME bursty arrivals to a token bucket and a leaky bucket,
// both configured with capacity 5 and rate 2/sec, and watch the outputs diverge.
//
//   Token bucket  -> absorbs the burst (up to capacity), releases immediately  -> BURSTY output
//   Leaky bucket  -> accepts up to capacity but releases at a fixed cadence     -> SMOOTH output
//
// Time is driven by a FakeTimeProvider so the run is fully deterministic/reproducible.

const double rate = 2.0;   // requests/sec sustained
const int capacity = 5;

await Scenario(
    "SCENARIO A — instantaneous burst of 10 (all arrive at t=0)",
    arrivalsMs: Enumerable.Repeat(0, 10).ToArray());

await Scenario(
    "SCENARIO B — same 10 requests, paced at the sustained rate (every 500ms)",
    arrivalsMs: Enumerable.Range(0, 10).Select(i => i * 500).ToArray());

return;

async Task Scenario(string title, int[] arrivalsMs)
{
    var origin = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
    var tokenClock = new FakeTimeProvider(origin);
    var leakyClock = new FakeTimeProvider(origin);
    var token = new TokenBucketRateLimiter(new TokenBucketOptions(capacity, rate), tokenClock);
    var leaky = new LeakyBucketRateLimiter(new LeakyBucketOptions(capacity, rate), leakyClock);

    Console.WriteLine();
    Console.WriteLine(title);
    Console.WriteLine($"(capacity={capacity}, rate={rate}/s)  interval between leaks = {1000 / rate:N0}ms");
    Console.WriteLine(new string('-', 78));
    Console.WriteLine($"{"req",-4}{"arrival",-10}{"TOKEN BUCKET",-30}{"LEAKY BUCKET",-24}");

    var tokenOut = new List<double>();
    var leakyOut = new List<double>();

    for (var i = 0; i < arrivalsMs.Length; i++)
    {
        var arrival = arrivalsMs[i];
        tokenClock.SetUtcNow(origin.AddMilliseconds(arrival));
        leakyClock.SetUtcNow(origin.AddMilliseconds(arrival));

        var t = await token.CheckAsync("client");
        var l = await leaky.CheckAsync("client");

        // Token bucket releases accepted requests immediately (output time == arrival).
        var tokenMsg = t.Allowed ? $"ACCEPT  out@{arrival}ms" : "reject";
        if (t.Allowed) tokenOut.Add(arrival);

        // Leaky bucket releases at the scheduled (smoothed) time carried in ResetsAt.
        double? leakyRelease = l.Allowed ? (l.ResetsAt - origin).TotalMilliseconds : null;
        var leakyMsg = l.Allowed ? $"ACCEPT  out@{leakyRelease:N0}ms" : "DROP (overflow)";
        if (leakyRelease is not null) leakyOut.Add(leakyRelease.Value);

        Console.WriteLine($"{i + 1,-4}{arrival + "ms",-10}{tokenMsg,-30}{leakyMsg,-24}");
    }

    Console.WriteLine();
    Console.WriteLine("  output timeline (each dot = one request released downstream):");
    Console.WriteLine("  token: " + Timeline(tokenOut));
    Console.WriteLine("  leaky: " + Timeline(leakyOut));
}

// Render release times onto a coarse 0..3000ms axis (100ms per column) to make bursts vs smoothing visible.
static string Timeline(List<double> outputsMs)
{
    const int columns = 30; // 0..3000ms at 100ms resolution
    var slots = new int[columns];
    foreach (var ms in outputsMs)
    {
        var col = (int)Math.Round(ms / 100.0);
        if (col >= 0 && col < columns) slots[col]++;
    }
    return string.Concat(slots.Select(c => c == 0 ? '.' : c > 1 ? '#' : 'o'));
}
