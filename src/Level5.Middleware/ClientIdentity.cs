using System.Security.Claims;

namespace Level5.Middleware;

/// <summary>
/// Helpers for pulling the various client identities out of a request. Each rate-limit dimension
/// keys on one of these.
/// </summary>
public static class ClientIdentity
{
    public static string Ip(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public static bool IsAuthenticated(HttpContext ctx) =>
        ctx.User.Identity?.IsAuthenticated == true;

    public static string? UserId(HttpContext ctx) =>
        ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    public static string? ApiKey(HttpContext ctx) =>
        ctx.Request.Headers["X-Api-Key"].FirstOrDefault();
}

/// <summary>
/// A STAND-IN for real authentication. If the request carries an "X-User-Id" header we treat the
/// caller as an authenticated user with that id. A production app would validate a JWT / cookie and
/// populate <see cref="HttpContext.User"/> from that; the rate-limiting logic downstream is identical.
/// </summary>
public sealed class SimulatedAuthMiddleware
{
    private readonly RequestDelegate _next;
    public SimulatedAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var userId = context.Request.Headers["X-User-Id"].FirstOrDefault();
        if (!string.IsNullOrEmpty(userId))
        {
            var identity = new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, authenticationType: "Simulated");
            context.User = new ClaimsPrincipal(identity);
        }

        await _next(context);
    }
}
