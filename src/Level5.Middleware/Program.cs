using Level5.Middleware;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.TimestampFormat = "HH:mm:ss.fff ";
    o.UseUtcTimestamp = true;
    o.SingleLine = true;
});

var cfg = builder.Configuration;
var redisConn = cfg.GetValue("Redis:ConnectionString", "localhost:6379")!;
var keyNamespace = cfg.GetValue("RateLimit:KeyNamespace", "")!;
var window = TimeSpan.FromSeconds(cfg.GetValue("RateLimit:WindowSeconds", 60));
var whitelistToken = cfg.GetValue("RateLimit:WhitelistToken", "internal-secret");

// Per-dimension limits. Anonymous is deliberately the tightest; an authenticated user gets a higher,
// per-user allowance; api keys get their own budget; the per-IP cap is a coarse anti-abuse net.
var ipLimit = cfg.GetValue("RateLimit:Ip:Limit", 100L);
var anonLimit = cfg.GetValue("RateLimit:Anon:Limit", 5L);
var userLimit = cfg.GetValue("RateLimit:User:Limit", 20L);
var apiKeyLimit = cfg.GetValue("RateLimit:ApiKey:Limit", 50L);

var redisConfig = ConfigurationOptions.Parse(redisConn);
redisConfig.AbortOnConnectFail = false;
var multiplexer = await ConnectionMultiplexer.ConnectAsync(redisConfig);

// The ordered chain: IP -> anon -> user -> api key. Anonymous and user are mutually exclusive
// (their selectors return null when not applicable), which is how one chain serves both.
var chain = new[]
{
    new RateLimitPolicy
    {
        Name = "ip", Limit = ipLimit, Window = window,
        KeySelector = ClientIdentity.Ip   // always applies: a coarse per-host cap
    },
    new RateLimitPolicy
    {
        Name = "anon", Limit = anonLimit, Window = window,
        KeySelector = ctx => ClientIdentity.IsAuthenticated(ctx) ? null : ClientIdentity.Ip(ctx)
    },
    new RateLimitPolicy
    {
        Name = "user", Limit = userLimit, Window = window,
        KeySelector = ctx => ClientIdentity.IsAuthenticated(ctx) ? ClientIdentity.UserId(ctx) : null
    },
    new RateLimitPolicy
    {
        Name = "apikey", Limit = apiKeyLimit, Window = window,
        KeySelector = ClientIdentity.ApiKey   // applies only when X-Api-Key is present
    },
};

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IConnectionMultiplexer>(multiplexer);
builder.Services.AddSingleton(new RateLimitPolicyRegistry(chain));
builder.Services.AddSingleton(sp => new RedisRateLimiterProvider(
    sp.GetRequiredService<IConnectionMultiplexer>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILoggerFactory>(),
    keyNamespace));
builder.Services.AddSingleton(new RateLimitBypassOptions(whitelistToken));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseMiddleware<SimulatedAuthMiddleware>();          // populate HttpContext.User (stand-in auth)
app.UseMiddleware<CompositeRateLimitingMiddleware>();  // then enforce the policy chain

app.MapGet("/api/resource", (HttpContext ctx) => Results.Ok(new
{
    data = "you got the resource",
    identity = ClientIdentity.IsAuthenticated(ctx) ? $"user:{ClientIdentity.UserId(ctx)}" : $"anon:{ClientIdentity.Ip(ctx)}"
}));

app.MapGet("/debug/state", (RateLimitPolicyRegistry registry) => Results.Ok(new
{
    windowSeconds = window.TotalSeconds,
    whitelistHeader = "X-Internal-Token",
    chain = registry.Chain.Select(p => new { p.Name, p.Limit, algorithm = p.Algorithm.ToString() })
}));

app.Run();

public partial class Program;
