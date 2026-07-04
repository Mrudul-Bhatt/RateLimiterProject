using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace Level4.Tests;

/// <summary>
/// Spins up a real Redis in a throwaway container (via Testcontainers) once for the whole test
/// collection. Requires a running Docker daemon. Each test uses unique keys / flushes as needed.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7-alpine").Build();

    public IConnectionMultiplexer Redis { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Redis = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        if (Redis is not null) await Redis.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition("redis")]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>;
