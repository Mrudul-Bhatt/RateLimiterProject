using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Level6.Tests;

[Collection("quota")]
public class TieredQuotaTests
{
    private readonly QuotaFixture _fx;
    public TieredQuotaTests(QuotaFixture fx) => _fx = fx;

    private static FakeTimeProvider Clock(string utc = "2026-06-15T12:00:00Z") =>
        new(DateTimeOffset.Parse(utc));

    private static Task<HttpResponseMessage> Call(HttpClient c, string path, string user) =>
        c.SendAsync(new HttpRequestMessage(HttpMethod.Get, path) { Headers = { { "X-User-Id", user } } });

    private static Task<HttpResponseMessage> Basic(HttpClient c, string user) => Call(c, "/api/basic", user);
    private static Task<HttpResponseMessage> Image(HttpClient c, string user) => Call(c, "/api/image", user);

    private static async Task Upgrade(HttpClient c, string user, string tier)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/admin/quota/{user}")
        {
            Headers = { { "X-Admin-Token", QuotaFixture.AdminToken } },
            Content = JsonContent.Create(new { setTier = tier })
        };
        (await c.SendAsync(req)).EnsureSuccessStatusCode();
    }

    private static string? Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;

    [Fact]
    public async Task Free_tier_blocked_at_lower_limit_than_pro()
    {
        using var app = _fx.CreateApp(Clock());
        var client = app.CreateClient();

        // Free: rpm 5 -> only 5 of 6 succeed.
        var freeAllowed = 0;
        for (var i = 0; i < 6; i++)
            if ((await Basic(client, "free-user")).IsSuccessStatusCode) freeAllowed++;

        // Pro (rpm 60): provision, upgrade, then 6 all succeed.
        await Basic(client, "pro-user");           // provision as Free
        await Upgrade(client, "pro-user", "Pro");
        var proAllowed = 0;
        for (var i = 0; i < 6; i++)
            if ((await Basic(client, "pro-user")).IsSuccessStatusCode) proAllowed++;

        Assert.Equal(5, freeAllowed);
        Assert.Equal(6, proAllowed);
        Assert.True(proAllowed > freeAllowed);
    }

    [Fact]
    public async Task Per_minute_limit_trips_before_daily()
    {
        using var app = _fx.CreateApp(Clock()); // Free rpm=5 < rpd=100
        var client = app.CreateClient();

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await Basic(client, "u")).StatusCode);

        var blocked = await Basic(client, "u");
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal("Minute", Header(blocked, "X-RateLimit-Window"));  // minute trips, not day
    }

    [Fact]
    public async Task Image_request_debits_10_credits()
    {
        using var app = _fx.CreateApp(Clock());
        var client = app.CreateClient();

        var res = await Image(client, "u");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(10, body.GetProperty("monthUsed").GetInt64());
        Assert.Equal(10, body.GetProperty("cost").GetInt64());
    }

    [Fact]
    public async Task Daily_quota_resets_at_midnight_utc()
    {
        // Small daily cap; minute cap high so only the DAY window is in play.
        var clock = Clock("2026-06-15T23:59:30Z");
        using var app = _fx.CreateApp(clock,
            ("Plans:Free:Rpd", "3"), ("Plans:Free:Rpm", "100"));
        var client = app.CreateClient();

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await Basic(client, "u")).StatusCode);
        var blocked = await Basic(client, "u");
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal("Day", Header(blocked, "X-RateLimit-Window"));

        // Cross midnight UTC -> the date-keyed daily bucket resets.
        clock.SetUtcNow(DateTimeOffset.Parse("2026-06-16T00:00:30Z"));
        Assert.Equal(HttpStatusCode.OK, (await Basic(client, "u")).StatusCode);
    }

    [Fact]
    public async Task Paid_tier_allows_metered_overage()
    {
        // Tiny monthly credit budget (15). Free = Block, Pro = Meter. Each image costs 10.
        using var app = _fx.CreateApp(Clock(),
            ("Plans:Free:Credits", "15"), ("Plans:Pro:Credits", "15"));
        var client = app.CreateClient();

        // Free (Block): 1st image ok (10), 2nd would be 20 > 15 -> blocked on the month window.
        Assert.Equal(HttpStatusCode.OK, (await Image(client, "free")).StatusCode);
        var freeBlocked = await Image(client, "free");
        Assert.Equal(HttpStatusCode.TooManyRequests, freeBlocked.StatusCode);
        Assert.Equal("Month", Header(freeBlocked, "X-RateLimit-Window"));

        // Pro (Meter): provision, upgrade, then go past the budget — allowed, overage recorded.
        await Basic(client, "pro");
        await Upgrade(client, "pro", "Pro");
        await Image(client, "pro");                  // used ~11
        var over = await Image(client, "pro");       // used ~21 > 15 -> metered, still 200
        Assert.Equal(HttpStatusCode.OK, over.StatusCode);
        var body = await over.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("overage").GetInt64() > 0);
    }

    [Fact]
    public async Task Admin_can_adjust_user_quota_and_it_is_audited()
    {
        using var app = _fx.CreateApp(Clock());
        var client = app.CreateClient();

        await Basic(client, "u");                    // provision as Free
        await Upgrade(client, "u", "Enterprise");    // admin change

        // The account reflects the new tier.
        var state = await (await client.GetAsync("/debug/state?user=u")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Enterprise", state.GetProperty("account").GetProperty("tier").GetString());

        // And the change is in the audit trail.
        var auditReq = new HttpRequestMessage(HttpMethod.Get, "/admin/audit/u")
        {
            Headers = { { "X-Admin-Token", QuotaFixture.AdminToken } }
        };
        var audit = await (await client.SendAsync(auditReq)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(audit.EnumerateArray(), e => e.GetProperty("action").GetString() == "SetTier");
    }
}
