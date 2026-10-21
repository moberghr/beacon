using Beacon.Connector.MySql;
using Beacon.Connector.PostgreSql;
using Beacon.Connector.SqlServer;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Data.Enums;
using Beacon.Core.DataMigration;
using Beacon.Core.Services;
using Beacon.Core.SqlServer;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>Each connector registers exactly one migration writer for its engine, however often it is added.</summary>
[TestFixture]
[NonParallelizable] // Adding a connector re-registers DbConnectionFactory, a process-wide registry.
public class MigrationWriterRegistrationTests
{
    [Test]
    public void AddPostgreSqlConnector_RegistersOnePostgreSqlWriter_EvenWhenAddedTwice()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = new BeaconBuilder(services, new ConfigurationBuilder().Build());

        builder.AddPostgreSqlConnector();
        builder.AddPostgreSqlConnector();
        using var provider = services.BuildServiceProvider();

        var writers = provider.GetServices<IMigrationBulkWriter>().ToList();
        writers.Should().ContainSingle()
            .Which.Engine.Should().Be(DatabaseEngineType.PostgreSQL);
        writers[0].GetType().Assembly.GetName().Name.Should().Be("Beacon.Connector.PostgreSql");
        new MigrationBulkWriters(writers).Require(DatabaseEngineType.PostgreSQL).Should().BeSameAs(writers[0]);
    }

    [Test]
    public void AddSqlServerConnector_RegistersOneSqlServerWriter_EvenWhenAddedTwice()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = new BeaconBuilder(services, new ConfigurationBuilder().Build());

        builder.AddSqlServerConnector();
        builder.AddSqlServerConnector();
        using var provider = services.BuildServiceProvider();

        var writers = provider.GetServices<IMigrationBulkWriter>().ToList();
        writers.Should().ContainSingle()
            .Which.Engine.Should().Be(DatabaseEngineType.MSSQL);
        writers[0].GetType().Assembly.GetName().Name.Should().Be("Beacon.Connector.SqlServer");
        new MigrationBulkWriters(writers).Require(DatabaseEngineType.MSSQL).Should().BeSameAs(writers[0]);
    }

    [Test]
    public void AddMySqlConnector_RegistersOneMySqlWriter_EvenWhenAddedTwice()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = new BeaconBuilder(services, new ConfigurationBuilder().Build());

        builder.AddMySqlConnector();
        builder.AddMySqlConnector();
        using var provider = services.BuildServiceProvider();

        var writers = provider.GetServices<IMigrationBulkWriter>().ToList();
        writers.Should().ContainSingle()
            .Which.Engine.Should().Be(DatabaseEngineType.MySQL);
        writers[0].GetType().Assembly.GetName().Name.Should().Be("Beacon.Connector.MySql");
        new MigrationBulkWriters(writers).Require(DatabaseEngineType.MySQL).Should().BeSameAs(writers[0]);
    }

    [Test]
    public void EveryConnector_ServesItsOwnEngine()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = new BeaconBuilder(services, new ConfigurationBuilder().Build());

        builder.AddPostgreSqlConnector();
        builder.AddSqlServerConnector();
        builder.AddMySqlConnector();
        using var provider = services.BuildServiceProvider();

        var writers = new MigrationBulkWriters(provider.GetServices<IMigrationBulkWriter>());
        writers.Require(DatabaseEngineType.PostgreSQL).GetType().Assembly.GetName().Name.Should().Be("Beacon.Connector.PostgreSql");
        writers.Require(DatabaseEngineType.MSSQL).GetType().Assembly.GetName().Name.Should().Be("Beacon.Connector.SqlServer");
        writers.Require(DatabaseEngineType.MySQL).GetType().Assembly.GetName().Name.Should().Be("Beacon.Connector.MySql");
    }

    // The host's container builds MigrationService with the writers its connectors registered.
    [Test]
    public void AHostWithEveryConnector_ResolvesMigrationService_WithEachEnginesWriter()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Beacon:EncryptionKey"] = TestEncryptionKey.Value })
            .Build();
        var builder = services.AddBeaconServices(configuration, x => x.AddBeaconScheduler<NoOpScheduler>());
        builder.AddPostgreSqlConnector();
        builder.AddSqlServerConnector();
        builder.AddMySqlConnector();
        builder.UseSqlServer("Server=localhost;Database=unused;Trusted_Connection=True;TrustServerCertificate=True");
        services.AddScoped(_ => Mock.Of<IBeaconUserContext>());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IMigrationService>().Should().BeOfType<MigrationService>();
        var writers = provider.GetRequiredService<MigrationBulkWriters>();
        writers.Require(DatabaseEngineType.PostgreSQL).Engine.Should().Be(DatabaseEngineType.PostgreSQL);
        writers.Require(DatabaseEngineType.MSSQL).Engine.Should().Be(DatabaseEngineType.MSSQL);
        writers.Require(DatabaseEngineType.MySQL).Engine.Should().Be(DatabaseEngineType.MySQL);
        provider.GetServices<IMigrationBulkWriter>().Should().HaveCount(3);
    }

    [TestCase(DatabaseEngineType.PostgreSQL, "*Moberg.Beacon.Connector.PostgreSql*AddPostgreSqlConnector()*")]
    [TestCase(DatabaseEngineType.MSSQL, "*Moberg.Beacon.Connector.SqlServer*AddSqlServerConnector()*")]
    [TestCase(DatabaseEngineType.MySQL, "*Moberg.Beacon.Connector.MySql*AddMySqlConnector()*")]
    [TestCase(DatabaseEngineType.SQLite, "Only a PostgreSQL, MySQL or SQL Server database can be a migration write target.")]
    public void WithoutAWriter_RequireNamesTheConnectorToAdd(DatabaseEngineType engine, string message)
    {
        var act = () => new MigrationBulkWriters([]).Require(engine);

        act.Should().Throw<Beacon.Core.Models.BeaconException>().WithMessage(message);
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
