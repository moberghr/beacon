using System.Data.Common;
using System.Security.Cryptography;
using Beacon.Connector.MySql;
using Beacon.Core;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Worker;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

// Pomelo 9.0.0 pins EF Core to 9.x, so Beacon.Core referencing it raised NU1608 in every EF Core 10 host — an error
// under TreatWarningsAsErrors. The MySQL bulk provider now comes from the MySQL connector through
// IMigrationBulkContextProvider; these tests pin that split.
[TestFixture]
public class MigrationBulkContextFactoryTests
{
    [Test]
    public void BeaconCore_DoesNotReferencePomeloOrMySqlConnector()
    {
        var references = typeof(BeaconContext).Assembly
            .GetReferencedAssemblies()
            .Select(x => x.Name)
            .ToList();

        references.Should().NotContain(x => x!.StartsWith("Pomelo", StringComparison.OrdinalIgnoreCase));
        references.Should().NotContain("MySqlConnector");
    }

    [Test]
    public void EnsureAvailable_MySqlWithoutConnector_ThrowsNamingThePackage()
    {
        var factory = new MigrationBulkContextFactory([]);

        var act = () => factory.EnsureAvailable(DatabaseEngineType.MySQL);

        act.Should().Throw<BeaconException>()
            .WithMessage("*Moberg.Beacon.Connector.MySql*AddMySqlConnector()*");
    }

    [Test]
    public void Create_MySqlWithoutConnector_ThrowsNamingThePackage()
    {
        var factory = new MigrationBulkContextFactory([]);
        using var connection = new NpgsqlConnection();

        var act = () => factory.Create(connection, null!, DatabaseEngineType.MySQL, "t", null);

        act.Should().Throw<BeaconException>()
            .WithMessage("*Moberg.Beacon.Connector.MySql*AddMySqlConnector()*");
    }

    [TestCase(DatabaseEngineType.PostgreSQL)]
    [TestCase(DatabaseEngineType.MSSQL)]
    public void EnsureAvailable_BuiltInEngines_NeedNoConnectorProvider(DatabaseEngineType engineType)
    {
        var factory = new MigrationBulkContextFactory([]);

        var act = () => factory.EnsureAvailable(engineType);

        act.Should().NotThrow();
    }

    [Test]
    public void Create_UsesTheRegisteredProviderForItsEngine()
    {
        var provider = new RecordingProvider(DatabaseEngineType.MySQL);
        var factory = new MigrationBulkContextFactory([provider]);
        using var connection = new NpgsqlConnection("Host=localhost;Database=unused");

        using var context = factory.Create(connection, null!, DatabaseEngineType.MySQL, "t", null);

        provider.ConfiguredWith.Should().BeSameAs(connection);
        context.Database.GetDbConnection().Should().BeSameAs(connection);
    }

    [Test]
    public void AddMySqlConnector_RegistersTheMySqlBulkProvider()
    {
        using var serviceProvider = BuildProvider(withMySqlConnector: true);

        var providers = serviceProvider.GetServices<IMigrationBulkContextProvider>().ToList();
        var factory = serviceProvider.GetRequiredService<MigrationBulkContextFactory>();

        providers.Should().ContainSingle(x => x.EngineType == DatabaseEngineType.MySQL)
            .Which.GetType().Assembly.GetName().Name.Should().Be("Beacon.Connector.MySql");
        factory.Invoking(x => x.EnsureAvailable(DatabaseEngineType.MySQL)).Should().NotThrow();
    }

    [Test]
    public void BeaconServicesAlone_RejectMySqlMigrationTargets()
    {
        using var serviceProvider = BuildProvider(withMySqlConnector: false);

        var factory = serviceProvider.GetRequiredService<MigrationBulkContextFactory>();

        factory.Invoking(x => x.EnsureAvailable(DatabaseEngineType.MySQL)).Should().Throw<BeaconException>();
    }

    private static ServiceProvider BuildProvider(bool withMySqlConnector)
    {
        var services = new ServiceCollection();

        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Beacon:EncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            })
            .Build();

        var builder = services.AddBeaconServices(configuration, x => x.AddBeaconScheduler<NoOpScheduler>());
        if (withMySqlConnector)
        {
            builder.AddMySqlConnector();
        }

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class RecordingProvider(DatabaseEngineType engineType) : IMigrationBulkContextProvider
    {
        public DbConnection? ConfiguredWith { get; private set; }

        public DatabaseEngineType EngineType => engineType;

        public void Configure(DbContextOptionsBuilder optionsBuilder, DbConnection connection)
        {
            ConfiguredWith = connection;
            optionsBuilder.UseNpgsql(connection);
        }
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
