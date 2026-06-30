using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Level1.Tests;

/// <summary>
/// Black-box tests through the real ASP.NET Core pipeline: spin up the app in-memory, hit the
/// protected endpoint with an HttpClient, and assert the HTTP contract (status codes + headers).
/// These use the real system clock, so they only exercise the within-window behaviour (a fast burst).
/// </summary>
public class EndpointIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public EndpointIntegrationTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Successful_request_carries_rate_limit_headers()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/resource");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("X-RateLimit-Limit"));
        Assert.True(response.Headers.Contains("X-RateLimit-Remaining"));
        Assert.True(response.Headers.Contains("X-RateLimit-Reset"));
    }

    [Fact]
    public async Task Exceeding_limit_returns_429_with_retry_after()
    {
        var client = _factory.CreateClient();

        // Default config: limit 5 per 10s. A quick burst of 6 trips the limiter on the 6th.
        HttpResponseMessage? last = null;
        for (var i = 0; i < 6; i++)
            last = await client.GetAsync("/api/resource");

        Assert.NotNull(last);
        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
        Assert.NotNull(last.Headers.RetryAfter);
    }
}
