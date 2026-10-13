using Beacon.Core;
using Beacon.Core.Data;
using Beacon.Core.PostgreSql;
using Beacon.Core.Worker;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NUnit.Framework;

namespace Beacon.Tests.Integration;

/// <summary>
/// Migrates Beacon end to end through a host-owned <see cref="NpgsqlDataSource"/> on a real PostgreSQL server with
/// pgvector available. Set <see cref="AdminConnectionStringEnvVar"/> to a superuser connection string (the test
/// creates and drops its own database); without it the test is inconclusive, so machines with no server stay green.
/// <c>docker run -d -e POSTGRES_PASSWORD=pw -p 5432:5432 pgvector/pgvector:pg17</c> is enough.
/// </summary>
[TestFixture]
public class PostgreSqlHostDataSourceMigrationTests
{
    public const string AdminConnectionStringEnvVar = "BEACON_TEST_PGVECTOR_ADMIN_CONNECTION_STRING";

    private string? _adminConnectionString;
    private string _database = null!;

    [SetUp]
    public async Task SetUp()
    {
        _adminConnectionString = Environment.GetEnvironmentVariable(AdminConnectionStringEnvVar);
        if (string.IsNullOrWhiteSpace(_adminConnectionString))
        {
            Assert.Inconclusive($"{AdminConnectionStringEnvVar} is not set.");
        }

        _database = $"beacon_ds_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(_adminConnectionString);
        await admin.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_database}\"", admin);
        await create.ExecuteNonQueryAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (string.IsNullOrWhiteSpace(_adminConnectionString))
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(_adminConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }

    [Test]
    public async Task UseBeacon_MigratesThroughTheHostDataSource_WithoutChangingItsSearchPath()
    {
        // One pooled connection, so the host's later query reuses the exact physical connection Beacon used.
        var hostConnectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = _database,
            MaxPoolSize = 1
        }.ConnectionString;
        await using var hostDataSource = NpgsqlDataSource.Create(hostConnectionString);
        await using var provider = BuildProvider(hostDataSource);

        ServiceConfiguration.UseBeacon(provider, createSchema: true);

        await using (var context = await provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContextAsync())
        {
            // Unqualified, exactly as KnowledgeGraphService / PgVectorColumnWriter issue it.
            var embeddings = await context.Database
                .SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM mcp_embeddings")
                .SingleAsync();
            embeddings.Should().Be(0);
            (await context.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        }

        await using var host = await hostDataSource.OpenConnectionAsync();
        (await ScalarAsync<string>(host, "SHOW search_path")).Should().Be("\"$user\", public");
        (await ScalarAsync<long>(host, "SELECT count(*) FROM beacon.\"__EFMigrationsHistory\"")).Should().BePositive();
        (await ScalarAsync<string>(host,
                "SELECT udt_name::text FROM information_schema.columns "
                + "WHERE table_schema = 'beacon' AND table_name = 'mcp_embeddings' AND column_name = 'embedding'"))
            .Should().Be("vector");
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static ServiceProvider BuildProvider(NpgsqlDataSource hostDataSource)
    {
        var services = new ServiceCollection();
        services.AddSingleton(hostDataSource);

        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Beacon:EncryptionKey"] = Convert.ToBase64String(new byte[32])
            })
            .Build();

        services.AddBeaconServices(configuration, x => x.AddBeaconScheduler<NoOpScheduler>())
            .UsePostgreSql(sp => sp.GetRequiredService<NpgsqlDataSource>());

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
