using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Api;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.DataSources;
using Beacon.Core.Handlers.McpSettings;
using Beacon.Core.Models;
using Beacon.Core.Services.Providers;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Where a data source's engine does not refuse writes in Beacon's read-only session, that is shown and warned about:
/// the data-source entries carry it, saving such a source and starting the host log it (ids only), and the deployment
/// locks on the MCP safety settings are listed for the settings page and warned about when they are off.
/// </summary>
[TestFixture]
public class DataSourceReadOnlyWarningTests
{
    [TestCase(DataSourceType.Database, DatabaseEngineType.PostgreSQL, true)]
    [TestCase(DataSourceType.Database, DatabaseEngineType.MySQL, true)]
    [TestCase(DataSourceType.Database, DatabaseEngineType.MSSQL, false)]
    [TestCase(DataSourceType.Database, DatabaseEngineType.AzureSynapse, false)]
    [TestCase(DataSourceType.Database, DatabaseEngineType.Snowflake, false)]
    [TestCase(DataSourceType.BigQuery, null, false)]
    [TestCase(DataSourceType.Databricks, null, false)]
    [TestCase(DataSourceType.CloudWatch, null, null)]
    [TestCase(DataSourceType.Api, null, null)]
    public void TheEnforcement_IsWhatTheProviderReports_AndNullForSourcesWithoutSql(DataSourceType type, DatabaseEngineType? engine, bool? expected)
    {
        DataSourceReadOnlyEnforcement.Of(Providers(), type, engine).Should().Be(expected);
    }

    [Test]
    public void ASourceWhoseConnectorIsNotRegistered_IsNotEnforced()
    {
        var providers = new Mock<IDataSourceProviderFactory>();
        providers
            .Setup(x => x.GetProvider(It.IsAny<DataSourceType>()))
            .Throws(new BeaconException("No provider registered for data source type: BigQuery"));

        DataSourceReadOnlyEnforcement.Of(providers.Object, DataSourceType.BigQuery, null).Should().BeFalse();
    }

    [Test]
    public void TheDataSourceEntry_CarriesTheEnforcement()
    {
        var entry = new DataSourceEntry { Id = 9, DataSourceType = "Database", DatabaseEngineType = "MSSQL" };
        var postgres = entry with { DatabaseEngineType = "PostgreSQL" };
        var api = entry with { DataSourceType = "Api", DatabaseEngineType = null };

        DataSourceProjects.WithReadOnlyEnforcement(entry, Providers()).DatabaseReadOnlyEnforced.Should().BeFalse();
        DataSourceProjects.WithReadOnlyEnforcement(postgres, Providers()).DatabaseReadOnlyEnforced.Should().BeTrue();
        DataSourceProjects.WithReadOnlyEnforcement(api, Providers()).DatabaseReadOnlyEnforced.Should().BeNull();
    }

    [Test]
    public void SavingASourceWithoutEnforcement_LogsAWarningByIdOnly()
    {
        var logs = new LogRecorder();
        var logger = logs.For<DataSourceReadOnlyWarningTests>();

        DataSourceReadOnlyEnforcement.WarnIfNotEnforced(logger, Providers(), 17, DataSourceType.Database, DatabaseEngineType.MSSQL);
        DataSourceReadOnlyEnforcement.WarnIfNotEnforced(logger, Providers(), 18, DataSourceType.Database, DatabaseEngineType.PostgreSQL);
        DataSourceReadOnlyEnforcement.WarnIfNotEnforced(logger, Providers(), 19, DataSourceType.CloudWatch, null);

        var warning = logs.Entries.Should().ContainSingle().Subject;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Message.Should().Contain("17").And.Contain("read-only").And.Contain("login that can only read");
    }

    [Test]
    public async Task TheLocksQuery_ListsWhatTheDeploymentPins()
    {
        var locked = await new GetMcpDeploymentLocksHandler(Options.Create(new McpDeploymentOptions()))
            .Handle(new GetMcpDeploymentLocksQuery(), CancellationToken.None);
        var unlocked = await new GetMcpDeploymentLocksHandler(Options.Create(new McpDeploymentOptions { ForceReadOnly = false, ForcePiiDetection = false, ForceNoContentRetention = true }))
            .Handle(new GetMcpDeploymentLocksQuery(), CancellationToken.None);

        locked.LockedFields.Should().Equal("EnablePiiDetection", "EnforceReadOnly");
        unlocked.LockedFields.Should().Equal("RetainQueryContent");
    }

    [TestCase(true, true, 0)]
    [TestCase(false, true, 1)]
    [TestCase(false, false, 2)]
    public void Startup_WarnsAboutEveryMcpSafetyLockTheDeploymentTurnedOff(bool forceReadOnly, bool forcePiiDetection, int warnings)
    {
        var logs = new LogRecorder();
        var startup = new ReadOnlyStartupWarnings(
            Options.Create(new McpDeploymentOptions { ForceReadOnly = forceReadOnly, ForcePiiDetection = forcePiiDetection }),
            logs.For<ReadOnlyStartupWarnings>(),
            Options.Create(new MigrationOptions()));

        startup.Configure(_ => { });

        logs.Entries.Should().HaveCount(warnings);
        logs.Entries.Should().NotContain(x => x.Level != LogLevel.Warning || !x.Message.Contains("Beacon:Mcp:Force"));
    }

    // After start-up the data sources Beacon reads without a database-level read-only session are listed by id; a migration
    // write target is not, as Beacon never reads it, and neither is a source whose engine refuses writes.
    [Test]
    public async Task Startup_ListsTheReadSourcesWithoutEnforcement_ButNoWriteTarget()
    {
        var world = new ProjectAccessWorld();
        world.Source(41).DatabaseEngineType = DatabaseEngineType.Snowflake;
        var target = world.Source(52);
        target.DatabaseEngineType = DatabaseEngineType.MSSQL;
        target.AllowMigrationWrites = true;
        world.Source(63).DatabaseEngineType = DatabaseEngineType.PostgreSQL;
        world.Source(74).DatabaseEngineType = DatabaseEngineType.MSSQL;
        var services = new ServiceCollection();
        services.AddSingleton(world.Factory());
        services.AddSingleton(Providers());
        await using var provider = services.BuildServiceProvider();
        var logs = new LogRecorder();
        var startup = new ReadOnlyStartupWarnings(
            Options.Create(new McpDeploymentOptions()),
            logs.For<ReadOnlyStartupWarnings>(),
            Options.Create(new MigrationOptions { Enabled = true }));

        await startup.WarnAboutDataSourcesAsync(provider, CancellationToken.None);

        var warning = logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning).Subject;
        warning.Message.Should().Contain("41").And.Contain("74").And.Contain("login that can only read");
        warning.Message.Should().NotContain("52", "Beacon never reads a migration write target").And.NotContain("63");
    }

    [Test]
    public void TheStoredSettingsALockOverrides_AreFoundById_AndAnInheritedOverrideIsNot()
    {
        var overrides = McpLockPolicy.Overridden(
            new McpDeploymentOptions(),
            [(1, new McpSettingsData { EnforceReadOnly = false }), (2, new McpSettingsData())],
            [
                (7, new McpProjectSettingsData { EnablePiiDetection = false }),
                (8, new McpProjectSettingsData { EnforceReadOnly = true }),
                (9, new McpProjectSettingsData { RetainQueryContent = true })
            ]);
        var unlocked = McpLockPolicy.Overridden(
            new McpDeploymentOptions { ForceReadOnly = false, ForcePiiDetection = false },
            [(1, new McpSettingsData { EnforceReadOnly = false })],
            [(7, new McpProjectSettingsData { EnablePiiDetection = false })]);

        overrides.GlobalRowIds.Should().Equal(1);
        overrides.ProjectIds.Should().Equal([7], "an override that agrees with the lock, or one of an unlocked field, is not overridden");
        overrides.LockNames.Should().Equal("ForcePiiDetection", "ForceReadOnly");
        unlocked.Any.Should().BeFalse();
    }

    [Test]
    public void Startup_WarnsOnceAboutTheStoredSettingsALockOverrides_ByIdOnly()
    {
        var logs = new LogRecorder();
        var logger = logs.For<ReadOnlyStartupWarnings>();

        ReadOnlyStartupWarnings.WarnAboutOverriddenSettings(logger, new McpLockOverrides([], [], []));
        ReadOnlyStartupWarnings.WarnAboutOverriddenSettings(logger, new McpLockOverrides([1], [7, 9], ["ForceReadOnly"]));

        var warning = logs.Entries.Should().ContainSingle().Subject;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Message.Should().Contain("ForceReadOnly").And.Contain("Beacon:Mcp").And.Contain("1").And.Contain("7").And.Contain("9");
    }

    private static IDataSourceProviderFactory Providers()
    {
        var database = new DatabaseProvider(
            Mock.Of<Beacon.Core.HostData.IDataSourceConnectionResolver>(),
            new Beacon.Core.Services.Validation.SqlReadOnlyAstValidator(NullLogger<Beacon.Core.Services.Validation.SqlReadOnlyAstValidator>.Instance),
            Mock.Of<Beacon.Core.HostData.IHostDataSourceGuard>(),
            TestConfiguredDatabases.None,
            NullLogger<DatabaseProvider>.Instance,
            TestQueryServices.QueryOptions);
        var providers = new Mock<IDataSourceProviderFactory>();
        providers
            .Setup(x => x.GetProvider(DataSourceType.Database))
            .Returns(database);
        providers
            .Setup(x => x.GetProvider(It.Is<DataSourceType>(y => y != DataSourceType.Database)))
            .Returns<DataSourceType>(x => Mock.Of<IDataSourceProvider>(y => y.SupportedType == x));

        return providers.Object;
    }
}
