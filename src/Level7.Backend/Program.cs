// The protected backend service. In production this is your real API; here it's a trivial echo that
// also counts how many requests actually reached it — so you can *see* that rate-limited requests
// were stopped at the gateway and never got here.
var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(o => { o.TimestampFormat = "HH:mm:ss.fff "; o.SingleLine = true; });
var app = builder.Build();

var hits = 0;
app.MapGet("/_stats", () => Results.Ok(new { hitsReachedBackend = Volatile.Read(ref hits) }));
app.MapGet("/{**path}", (string? path) =>
{
    Interlocked.Increment(ref hits);
    return Results.Ok(new { backend = "reached", path = path ?? "" });
});

app.Run();
