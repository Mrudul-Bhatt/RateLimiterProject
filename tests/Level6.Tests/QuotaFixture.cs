using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Xunit;

namespace Level6.Tests;

/// <summary>
/// Starts a real Postgres AND a real Redis (Testcontainers) once for the whole test collection.
/// Each test builds an app via <see cref="CreateApp"/> pointed at its OWN Postgres database (so the
/// seeded plans are per-test and isolated) and a controllable <see cref="FakeTimeProvider"/>.
/// </summary>
public sealed class QuotaFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
    }

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
    }

    public const string AdminToken = "test-admin";

    /// <summary>Base Postgres connection string with the database swapped to a per-test name.</summary>
    private string PostgresFor(string db)
    {
        var baseConn = _postgres.GetConnectionString();
        // Replace the Database=... segment with our per-test database name.
        var parts = baseConn.Split(';').Select(p =>
            p.TrimStart().StartsWith("Database=", StringComparison.OrdinalIgnoreCase) ? $"Database={db}" : p);
        return string.Join(';', parts);
    }

    public WebApplicationFactory<Program> CreateApp(FakeTimeProvider clock, params (string key, string value)[] settings)
    {
        var db = $"l6_{Guid.NewGuid():N}";
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Redis:ConnectionString", _redis.GetConnectionString());
            b.UseSetting("Redis:KeyPrefix", $"{db}:");   // isolate the SHARED Redis per test
            b.UseSetting("Postgres:ConnectionString", PostgresFor(db));
            b.UseSetting("Admin:Token", AdminToken);
            foreach (var (k, v) in settings) b.UseSetting(k, v);

            b.ConfigureServices(s =>
            {
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(clock);
            });
        });
    }
}

[CollectionDefinition("quota")]
public sealed class QuotaCollection : ICollectionFixture<QuotaFixture>;
