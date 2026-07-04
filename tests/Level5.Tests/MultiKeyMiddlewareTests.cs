using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.Redis;
using Xunit;

namespace Level5.Tests;

/// <summary>
/// Integration tests through the real ASP.NET Core pipeline (WebApplicationFactory) against a real
/// Redis (Testcontainers). Each test spins the app up with its own key namespace + tuned limits, so
/// tests are isolated and deterministic.
/// </summary>
public class MultiKeyMiddlewareTests : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();

    public async Task InitializeAsync() => await _redis.StartAsync();
    public async Task DisposeAsync() => await _redis.DisposeAsync();

    private WebApplicationFactory<Program> CreateApp(params (string key, string value)[] settings)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Redis:ConnectionString", _redis.GetConnectionString());
            b.UseSetting("RateLimit:KeyNamespace", $"test-{Guid.NewGuid():N}");
            b.UseSetting("RateLimit:WindowSeconds", "60");
            b.UseSetting("RateLimit:WhitelistToken", "secret-internal");
            // High defaults so only the dimension a test cares about can trip.
            b.UseSetting("RateLimit:Ip:Limit", "1000");
            b.UseSetting("RateLimit:Anon:Limit", "1000");
            b.UseSetting("RateLimit:User:Limit", "1000");
            b.UseSetting("RateLimit:ApiKey:Limit", "1000");
            foreach (var (k, v) in settings) b.UseSetting(k, v);
        });
    }

    private static HttpRequestMessage Request(Action<HttpRequestMessage>? configure = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/resource");
        configure?.Invoke(req);
        return req;
    }

    [Fact]
    public async Task Headers_present_on_successful_requests()
    {
        using var app = CreateApp();
        var client = app.CreateClient();

        var res = await client.GetAsync("/api/resource");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(res.Headers.Contains("X-RateLimit-Limit"));
        Assert.True(res.Headers.Contains("X-RateLimit-Remaining"));
        Assert.True(res.Headers.Contains("X-RateLimit-Reset"));
        Assert.True(res.Headers.Contains("X-RateLimit-Dimension"));
    }

    [Fact]
    public async Task Returns_429_on_first_violated_dimension_with_correct_headers()
    {
        // Anonymous caller, anon limit = 3. The 4th request trips the "anon" dimension.
        using var app = CreateApp(("RateLimit:Anon:Limit", "3"));
        var client = app.CreateClient();

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/resource")).StatusCode);

        var blocked = await client.GetAsync("/api/resource");

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.NotNull(blocked.Headers.RetryAfter);
        Assert.Equal("anon", blocked.Headers.GetValues("X-RateLimit-Dimension").Single());

        var body = await blocked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("anon", body.GetProperty("dimension").GetString());
    }

    [Fact]
    public async Task Applies_all_three_limiters_in_order()
    {
        // Authenticated + api key => the applicable chain is ip -> user -> apikey (3 dimensions).
        // We make "user" the tightest (limit 2) and assert IT is the dimension reported — proving the
        // chain runs in order and fails fast on the first violated one (ip passes, user fails before
        // apikey is even checked).
        using var app = CreateApp(
            ("RateLimit:Ip:Limit", "1000"),
            ("RateLimit:User:Limit", "2"),
            ("RateLimit:ApiKey:Limit", "1000"));
        var client = app.CreateClient();

        void Auth(HttpRequestMessage r)
        {
            r.Headers.Add("X-User-Id", "alice");
            r.Headers.Add("X-Api-Key", "key-123");
        }

        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Request(Auth))).StatusCode);

        var blocked = await client.SendAsync(Request(Auth));
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal("user", blocked.Headers.GetValues("X-RateLimit-Dimension").Single());
    }

    [Fact]
    public async Task Authenticated_user_gets_higher_limit_than_anonymous()
    {
        using var app = CreateApp(("RateLimit:Anon:Limit", "3"), ("RateLimit:User:Limit", "8"));
        var client = app.CreateClient();

        // Anonymous: blocked after 3.
        var anonAllowed = 0;
        for (var i = 0; i < 10; i++)
            if ((await client.GetAsync("/api/resource")).IsSuccessStatusCode) anonAllowed++;
        Assert.Equal(3, anonAllowed);

        // Authenticated user: allowed up to 8 (its own, higher, per-user budget).
        var userAllowed = 0;
        for (var i = 0; i < 10; i++)
            if ((await client.SendAsync(Request(r => r.Headers.Add("X-User-Id", "bob")))).IsSuccessStatusCode) userAllowed++;
        Assert.Equal(8, userAllowed);

        Assert.True(userAllowed > anonAllowed);
    }

    [Fact]
    public async Task Whitelisted_service_token_bypasses_limiting()
    {
        // Anon limit 2, but the internal token skips ALL limiting — 20 requests all succeed.
        using var app = CreateApp(("RateLimit:Anon:Limit", "2"));
        var client = app.CreateClient();

        var allowed = 0;
        for (var i = 0; i < 20; i++)
        {
            var res = await client.SendAsync(Request(r => r.Headers.Add("X-Internal-Token", "secret-internal")));
            if (res.IsSuccessStatusCode) allowed++;
        }

        Assert.Equal(20, allowed);
    }
}
