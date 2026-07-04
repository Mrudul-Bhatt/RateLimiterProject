namespace Level5.Middleware;

/// <summary>
/// Holds named policies and the ordered CHAIN the middleware evaluates. This mirrors ASP.NET Core's
/// own <c>AddRateLimiter(...).AddPolicy(...)</c> registry — we build ours by hand to understand the
/// model, then you can compare.
///
/// Ordering matters: the chain is evaluated front-to-back and rejects on the FIRST violated
/// dimension (fail-fast). Put the cheapest / most-likely-to-fail checks first so you waste the least
/// work — and the fewest counter increments — on a request you're going to reject anyway.
/// </summary>
public sealed class RateLimitPolicyRegistry
{
    private readonly Dictionary<string, RateLimitPolicy> _byName;

    public RateLimitPolicyRegistry(IEnumerable<RateLimitPolicy> chain)
    {
        Chain = chain.ToList();
        _byName = Chain.ToDictionary(p => p.Name, StringComparer.Ordinal);
    }

    /// <summary>The ordered set of dimensions evaluated on every protected request.</summary>
    public IReadOnlyList<RateLimitPolicy> Chain { get; }

    public RateLimitPolicy Get(string name) => _byName[name];
}
