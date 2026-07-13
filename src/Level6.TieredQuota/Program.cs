using Level4.RedisAtomic;
using Level6.TieredQuota.Domain;
using Level6.TieredQuota.Quota;
using Microsoft.EntityFrameworkCore;
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
var pgConn = cfg.GetValue("Postgres:ConnectionString",
    "Host=localhost;Port=5432;Database=ratelimiter;Username=ratelimiter;Password=ratelimiter")!;
var adminToken = cfg.GetValue("Admin:Token", "admin-secret")!;

var redisConfig = ConfigurationOptions.Parse(redisConn);
redisConfig.AbortOnConnectFail = false;
var multiplexer = await ConnectionMultiplexer.ConnectAsync(redisConfig);

var keyPrefix = cfg.GetValue("Redis:KeyPrefix", "")!;

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new RedisKeyNamespace(keyPrefix));
builder.Services.AddSingleton<IConnectionMultiplexer>(multiplexer);
builder.Services.AddSingleton<RedisCostWindow>();
builder.Services.AddDbContext<QuotaDbContext>(o => o.UseNpgsql(pgConn));
builder.Services.AddScoped<QuotaEnforcementService>();
builder.Services.AddScoped<AdminQuotaService>();
builder.Services.AddHostedService(sp => new MonthlyResetService(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<MonthlyResetService>>()));

var app = builder.Build();

// Create schema + seed the tiers on startup. Plan limits are config-driven (defaults below) so a
// test can spin the app up with tiny limits to exercise a specific window.
Plan SeedPlan(Tier tier, int rpm, int rpd, long credits, OveragePolicy overage) => new()
{
    Tier = tier,
    RequestsPerMinute = cfg.GetValue($"Plans:{tier}:Rpm", rpm),
    RequestsPerDay = cfg.GetValue($"Plans:{tier}:Rpd", rpd),
    MonthlyCredits = cfg.GetValue($"Plans:{tier}:Credits", credits),
    Overage = Enum.Parse<OveragePolicy>(cfg.GetValue($"Plans:{tier}:Overage", overage.ToString())!, ignoreCase: true)
};
var seedPlans = new[]
{
    SeedPlan(Tier.Free,       rpm: 5,   rpd: 100,     credits: 1_000,     OveragePolicy.Block),
    SeedPlan(Tier.Pro,        rpm: 60,  rpd: 10_000,  credits: 100_000,   OveragePolicy.Meter),
    SeedPlan(Tier.Enterprise, rpm: 600, rpd: 500_000, credits: 5_000_000, OveragePolicy.Meter),
};
using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<QuotaDbContext>().InitializeAsync(seedPlans);

app.UseDefaultFiles();
app.UseStaticFiles();

// --- Metered API endpoints -------------------------------------------------
// Two endpoints with different COSTS: a basic call debits 1 credit, an image call debits 10.
app.MapGet("/api/basic", (HttpContext ctx, QuotaEnforcementService quota) => Consume(ctx, quota, cost: 1));
app.MapGet("/api/image", (HttpContext ctx, QuotaEnforcementService quota) => Consume(ctx, quota, cost: 10));

// --- Admin API (audited) ---------------------------------------------------
app.MapPost("/admin/quota/{userId}", async (
    string userId, AdminAdjustRequest req, HttpContext ctx, AdminQuotaService admin) =>
{
    if (ctx.Request.Headers["X-Admin-Token"] != adminToken)
        return Results.StatusCode(StatusCodes.Status403Forbidden);

    var tier = req.SetTier is null ? (Tier?)null : Enum.Parse<Tier>(req.SetTier, ignoreCase: true);
    var account = await admin.AdjustAsync(userId,
        new QuotaAdjustment(tier, req.ResetMonth ?? false, req.GrantCredits), adminId: "admin", ctx.RequestAborted);

    return account is null
        ? Results.NotFound(new { error = "account_not_found", userId })
        : Results.Ok(new { account.UserId, tier = account.Tier.ToString(), account.MonthCreditsUsed });
});

app.MapGet("/admin/audit/{userId}", async (string userId, HttpContext ctx, QuotaDbContext db) =>
{
    if (ctx.Request.Headers["X-Admin-Token"] != adminToken)
        return Results.StatusCode(StatusCodes.Status403Forbidden);

    var entries = await db.AuditEntries.AsNoTracking()
        .Where(a => a.UserId == userId).OrderByDescending(a => a.At)
        .Select(a => new { a.Action, a.Detail, a.By, a.At }).ToListAsync();
    return Results.Ok(entries);
});

// --- Debug / dashboard state ----------------------------------------------
app.MapGet("/debug/state", async (QuotaDbContext db, string? user) =>
{
    var plans = await db.Plans.AsNoTracking().OrderBy(p => p.MonthlyCredits)
        .Select(p => new { tier = p.Tier.ToString(), p.RequestsPerMinute, p.RequestsPerDay, p.MonthlyCredits, overage = p.Overage.ToString() })
        .ToListAsync();
    object? account = null;
    if (!string.IsNullOrEmpty(user))
        account = await db.Accounts.AsNoTracking().Where(a => a.UserId == user)
            .Select(a => new { a.UserId, tier = a.Tier.ToString(), a.MonthCreditsUsed, a.MonthAnchor }).FirstOrDefaultAsync();
    return Results.Ok(new { plans, account });
});

app.Run();

// Enforce the three quota windows for a request of the given cost, and translate to HTTP.
static async Task Consume(HttpContext ctx, QuotaEnforcementService quota, long cost)
{
    var userId = ctx.Request.Headers["X-User-Id"].FirstOrDefault();
    if (string.IsNullOrEmpty(userId))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new { error = "missing_user", message = "Send an X-User-Id header." });
        return;
    }

    var d = await quota.CheckAndDebitAsync(userId, cost, ctx.RequestAborted);
    var h = ctx.Response.Headers;
    h["X-RateLimit-Tier"] = d.Tier.ToString();
    h["X-Quota-Month-Limit"] = d.MonthLimit.ToString();
    h["X-Quota-Month-Remaining"] = d.MonthRemaining.ToString();
    if (d.MonthOverage > 0) h["X-Quota-Month-Overage"] = d.MonthOverage.ToString();

    if (d.Allowed)
    {
        await ctx.Response.WriteAsJsonAsync(new
        {
            ok = true, tier = d.Tier.ToString(), cost = d.Cost,
            monthUsed = d.MonthUsed, monthLimit = d.MonthLimit, overage = d.MonthOverage
        });
        return;
    }

    var retryAfter = (int)Math.Ceiling(d.RetryAfter.TotalSeconds);
    h["X-RateLimit-Window"] = d.LimitHit.ToString();
    h["Retry-After"] = retryAfter.ToString();
    ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
    await ctx.Response.WriteAsJsonAsync(new
    {
        error = "quota_exceeded", window = d.LimitHit.ToString(), tier = d.Tier.ToString(), retryAfterSeconds = retryAfter
    });
}

public record AdminAdjustRequest(string? SetTier, bool? ResetMonth, long? GrantCredits);

public partial class Program;
