using System.Text.RegularExpressions;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.HostData;
using Beacon.Core.HostDocs;
using Beacon.Core.Models;
using Beacon.Core.Notifications;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Validation;
using Beacon.Core.Worker;
using Beacon.Core.Worker.Services;
using Beacon.Connector.MySql;
using Beacon.Connector.PostgreSql;
using Beacon.Connector.SqlServer;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.ChangeGovernance;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.DataSourceDisable;

/// <summary>
/// A disabled data source makes no connection at all: every path of the connection resolver refuses it before anything is
/// decrypted or its transport checked, the refusal is never read as a cross-database verdict, the project listings MCP and
/// AI read leave it out, and a subscription run on it is recorded as failed with a generic reason. Disabling, enabling and
/// replacing a connection are staged security events in the unit of work of their change, naming no value; a change of
/// target is saved only when confirmed, and then removes the column values sampled from the old target. Recording doubles,
/// no database (§4.7).
/// </summary>
[TestFixture]
[NonParallelizable] // DbConnectionFactory is a process-wide registry.
public class DataSourceDisableTests
{
    private const int SourceId = 12;
    private const int SubscriptionId = 21;
    private const int AdaId = 7;
    private const string OldConnection = "Host=db-a.internal;Database=sales;Username=reader;Password=old-pass;SSL Mode=VerifyFull";
    private const string RotatedConnection = "Host=db-a.internal;Database=sales;Username=reader;Password=new-pass;SSL Mode=VerifyFull";
    private const string MovedConnection = "Host=db-b.internal;Database=sales;Username=reader;Password=new-pass;SSL Mode=VerifyFull";

    private RecordingSecurityAudit _audit = null!;

    [SetUp]
    public void SetUp()
    {
        _audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(AdaId, "ext-ada", isAdmin: true));
    }

    [TearDown]
    public void TearDown()
    {
        // As the connectors register them.
        new BeaconBuilder(new ServiceCollection(), new ConfigurationBuilder().Build())
            .AddPostgreSqlConnector()
            .AddSqlServerConnector()
            .AddMySqlConnector();
    }

    // Strict doubles: a decrypt or a transport check would throw a MockException, not the refusal.
    [TestCase("read")]
    [TestCase("data")]
    [TestCase("write")]
    public void EveryConnectionPath_RefusesADisabledSource_BeforeDecryptingOrCheckingItsTransport(string path)
    {
        var resolver = new DataSourceConnectionResolver(
            Mock.Of<IEncryptionService>(MockBehavior.Strict),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            Mock.Of<IDataSourceTransportPolicy>(MockBehavior.Strict));
        var source = Source(disabled: true, allowMigrationWrites: path == "write");

        Func<string> connect = path switch
        {
            "read" => () => resolver.GetConnectionString(source),
            "data" => () => resolver.GetConnectionData(source),
            _ => () => resolver.GetWriteConnectionString(source)
        };

        connect.Should().Throw<DataSourceDisabledException>()
            .Which.Message.Should().Be(DataSourceDisabledException.RefusalMessage)
            .And.NotContain("warehouse");
    }

    [Test]
    public void AnEnabledSource_StillConnects()
    {
        var resolver = TestTransport.Resolver(Encryption());

        resolver.GetConnectionString(Source(disabled: false)).Should().Be(OldConnection);
    }

    [Test]
    public void TheConfiguredDatabaseOfADisabledSource_IsTheRefusal_NeverACrossDatabaseVerdict()
    {
        var resolver = new ConfiguredDatabaseResolver(TestTransport.Resolver(Encryption()), NullLogger<ConfiguredDatabaseResolver>.Instance);

        var act = () => resolver.Resolve(Source(disabled: true));

        act.Should().Throw<DataSourceDisabledException>();
    }

    // The links kept are those whose data source has no disabled time: the predicate's polarity, on both providers.
    [TestCase(false)]
    [TestCase(true)]
    public void WhereReadable_LeavesDisabledSourcesOut_Translates(bool sqlServer)
    {
        using var context = Context(sqlServer);

        var sql = context.ProjectDataSources
            .WhereReadable()
            .Where(x => x.ProjectId == 4)
            .Select(x => x.DataSourceId)
            .ToQueryString();

        ShouldKeepOnlyEnabledSources(sql, sqlServer);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TheProjectBriefsDataSources_LeaveDisabledSourcesOut_Translates(bool sqlServer)
    {
        using var context = Context(sqlServer);

        var sql = ProjectBriefService.BuildDataSourcesQuery(context, 4).ToQueryString();

        ShouldKeepOnlyEnabledSources(sql, sqlServer);
    }

    [Test]
    public async Task AScheduledRunOnADisabledSource_IsRecordedFailed_WithAGenericReason_AndConnectsNothing()
    {
        var store = Subscribed();
        var connections = new Mock<IDataSourceConnectionResolver>(MockBehavior.Strict);

        await Jobs(store, connections.Object, request: null).ExecuteQuery(SubscriptionId, CancellationToken.None);

        ShouldBeFailed(store);
    }

    [Test]
    public async Task ARequestedRunOnADisabledSource_IsRecordedFailed_AndAnswered409()
    {
        var store = Subscribed();
        var connections = new Mock<IDataSourceConnectionResolver>(MockBehavior.Strict);

        var act = () => Jobs(store, connections.Object, request: new DefaultHttpContext()).ExecuteQuery(SubscriptionId, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(NotificationFailureReasons.DataSourceDisabled);
        ShouldBeFailed(store);
    }

    [Test]
    public async Task Disabling_RecordsWhoAndWhen_StagesAnIdsOnlyEvent_AndEnablingClearsIt()
    {
        var source = Source(disabled: false);
        var world = new World(source);

        await world.Service(_audit).SetDataSourceEnabled(SourceId, enabled: false, AdaId, CancellationToken.None);

        source.DisabledTime.Should().NotBeNull();
        source.DisabledByUserId.Should().Be(AdaId);
        _audit.Single("warehouse", "old-pass", "db-a.internal").Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.DataSourceDisabled, ResourceType = SecurityAuditResources.DataSource, ResourceId = "12", DetailsJson = (string?)null },
            x => x.ExcludingMissingMembers());

        await world.Service(_audit).SetDataSourceEnabled(SourceId, enabled: true, AdaId, CancellationToken.None);

        source.DisabledTime.Should().BeNull();
        source.DisabledByUserId.Should().BeNull();
        _audit.Events.Select(x => x.Action).Should().Equal(SecurityAuditActions.DataSourceDisabled, SecurityAuditActions.DataSourceEnabled);
        _audit.Ways.Should().Equal("staged", "staged");
        _audit.AssertStagedEventsSaved();
        world.Saves.Should().Be(2, "each change and its event are one unit of work");
    }

    [Test]
    public async Task SettingTheStateASourceAlreadyHas_ChangesAndRecordsNothing()
    {
        var source = Source(disabled: false);
        var world = new World(source);

        await world.Service(_audit).SetDataSourceEnabled(SourceId, enabled: true, AdaId, CancellationToken.None);

        _audit.Events.Should().BeEmpty();
        world.Saves.Should().Be(0);
    }

    [Test]
    public async Task Disabling_EmptiesTheDriverPoolOfItsConnection()
    {
        var cleared = RecordPoolClears();
        var world = new World(Source(disabled: false));

        await world.Service(_audit).SetDataSourceEnabled(SourceId, enabled: false, AdaId, CancellationToken.None);

        cleared.Should().Equal(OldConnection);
    }

    [Test]
    public async Task AHostManagedSource_CannotBeDisabledOrEdited()
    {
        var source = Source(disabled: false);
        source.HostManagedKey = "host";
        var world = new World(source);

        var disable = () => world.Service(_audit).SetDataSourceEnabled(SourceId, enabled: false, AdaId, CancellationToken.None);
        var edit = () => world.Service(_audit).UpdateDataSource(Edit(RotatedConnection), CancellationToken.None);

        await disable.Should().ThrowAsync<InvalidOperationException>();
        await edit.Should().ThrowAsync<InvalidOperationException>();
        source.DisabledTime.Should().BeNull();
        _audit.Events.Should().BeEmpty();
    }

    [TestCase(null)]
    [TestCase("")]
    public async Task AnEditWithoutAConnection_KeepsTheStoredOne(string? connection)
    {
        var source = Source(disabled: false);
        var world = new World(source);

        await world.Service(_audit).UpdateDataSource(Edit(connection, name: "warehouse-eu"), CancellationToken.None);

        source.EncryptedConnectionData.Should().Be(Encrypted(OldConnection));
        source.Name.Should().Be("warehouse-eu");
        _audit.Single("old-pass", "db-a.internal").Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.DataSourceUpdated, DetailsJson = "{\"connectionChanged\":false,\"targetChanged\":false,\"changedFields\":[\"Name\"]}" },
            x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task ReplacingTheCredentials_ToTheSameTarget_IsARotation_ThatEmptiesTheOldPool()
    {
        var cleared = RecordPoolClears();
        var source = Source(disabled: false);
        var world = new World(source);

        await world.Service(_audit).UpdateDataSource(Edit(RotatedConnection), CancellationToken.None);

        source.EncryptedConnectionData.Should().Be(Encrypted(RotatedConnection));
        _audit.Single("old-pass", "new-pass", "db-a.internal").Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.CredentialsRotated, DetailsJson = "{\"connectionChanged\":true,\"targetChanged\":false,\"changedFields\":[]}" },
            x => x.ExcludingMissingMembers());
        _audit.Ways.Should().Equal("staged");
        cleared.Should().Equal(OldConnection);
    }

    [Test]
    public async Task ATargetChange_WithoutConfirmation_IsRefused_AndNothingChanges()
    {
        var source = Source(disabled: false);
        var sampled = Sampled(out var world, source);

        var act = () => world.Service(_audit).UpdateDataSource(Edit(MovedConnection), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(DataSourceService.TargetChangeUnconfirmedMessage);
        source.EncryptedConnectionData.Should().Be(Encrypted(OldConnection));
        sampled.SampleValues.Should().NotBeNull();
        _audit.Events.Should().BeEmpty();
        world.Saves.Should().Be(0);
    }

    [Test]
    public async Task AnEngineChange_WithoutConfirmation_IsRefused()
    {
        var world = new World(Source(disabled: false));

        var act = () => world.Service(_audit).UpdateDataSource(
            Edit("Server=db-a.internal;Database=sales;User Id=reader;Password=new-pass;Encrypt=Strict", engine: DatabaseEngineType.MSSQL),
            CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>();
        _audit.Events.Should().BeEmpty();
    }

    [Test]
    public async Task AConfirmedTargetChange_ClearsTheSampledValuesAndCaches_InTheUnitOfWorkOfItsEvent()
    {
        var source = Source(disabled: false);
        var sampled = Sampled(out var world, source);
        world.Cache.Set(DatabaseMetadataService.GetCacheKey(SourceId), "stale");

        await world.Service(_audit).UpdateDataSource(Edit(MovedConnection, confirm: true), CancellationToken.None);

        source.EncryptedConnectionData.Should().Be(Encrypted(MovedConnection));
        sampled.SampleValues.Should().BeNull();
        sampled.SampleValuesComplete.Should().BeFalse();
        world.Cache.TryGetValue(DatabaseMetadataService.GetCacheKey(SourceId), out _).Should().BeFalse();
        world.Graph.Verify(x => x.Invalidate(SourceId), Times.Once);
        _audit.Single("old-pass", "new-pass", "db-a.internal", "db-b.internal", "sales").Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.CredentialsRotated, DetailsJson = "{\"connectionChanged\":true,\"targetChanged\":true,\"changedFields\":[]}" },
            x => x.ExcludingMissingMembers());
        _audit.Ways.Should().Equal("staged");
        _audit.AssertStagedEventsSaved();
        world.Saves.Should().Be(1, "the cleared values, the new connection and the event are one unit of work");
    }

    // The target is the host (any case), the port and the database (exact case) the engine's transport check reads.
    [TestCase("Host=db-a.internal;Port=5433;Database=sales;Username=reader;Password=new-pass;SSL Mode=VerifyFull", true)]
    [TestCase("Host=db-a.internal;Database=archive;Username=reader;Password=new-pass;SSL Mode=VerifyFull", true)]
    [TestCase("Host=db-a.internal;Database=Sales;Username=reader;Password=new-pass;SSL Mode=VerifyFull", true)]
    [TestCase("Host=DB-A.Internal;Database=sales;Username=reader;Password=new-pass;SSL Mode=VerifyFull", false)]
    public async Task WhatCountsAsATargetChange(string connection, bool targetChanged)
    {
        var source = Source(disabled: false);
        var world = new World(source);

        var act = () => world.Service(_audit).UpdateDataSource(Edit(connection), CancellationToken.None);

        if (targetChanged)
        {
            await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(DataSourceService.TargetChangeUnconfirmedMessage);
            source.EncryptedConnectionData.Should().Be(Encrypted(OldConnection));
            _audit.Events.Should().BeEmpty();

            return;
        }

        await act.Should().NotThrowAsync();
        _audit.Single().DetailsJson.Should().Contain("\"targetChanged\":false");
    }

    [Test]
    public async Task AStoredConnectionThatCannotBeRead_CannotBeKept_AndIsLoggedWithoutAValue()
    {
        var source = Source(disabled: false);
        source.EncryptedConnectionData = "unreadable-stored-value";
        var logs = new LogRecorder();
        var world = new World(source);

        var act = () => world.Service(_audit, logger: logs.For<DataSourceService>()).UpdateDataSource(Edit(null, name: "renamed"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cannot be kept*");
        source.Name.Should().Be("warehouse");
        _audit.Events.Should().BeEmpty();
        var warning = logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning && x.Message.Contains("could not be decrypted", StringComparison.Ordinal)).Subject;
        warning.Message.Should().Contain("12").And.Contain("PostgreSQL").And.Contain(nameof(System.Security.Cryptography.CryptographicException));
        warning.FullText.Should().NotContain("unreadable-stored-value");
    }

    [Test]
    public async Task ANewConnection_OverAStoredOneThatCannotBeRead_IsATargetChange()
    {
        var source = Source(disabled: false);
        source.EncryptedConnectionData = "unreadable-stored-value";
        var world = new World(source);

        var act = () => world.Service(_audit).UpdateDataSource(Edit(RotatedConnection), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(DataSourceService.TargetChangeUnconfirmedMessage);
        source.EncryptedConnectionData.Should().Be("unreadable-stored-value");
    }

    [Test]
    public async Task ATargetTheTransportCheckCannotRead_IsATargetChange()
    {
        var transport = new Mock<IDataSourceTransportPolicy>();
        transport
            .Setup(x => x.Inspect(It.IsAny<DataSourceType>(), It.IsAny<DatabaseEngineType?>(), It.IsAny<string>()))
            .Returns(DataSourceTransportVerdict.Pass(target: null));
        var world = new World(Source(disabled: false));

        var act = () => world.Service(_audit, transport.Object).UpdateDataSource(Edit(RotatedConnection), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>();
        _audit.Events.Should().BeEmpty();
    }

    [Test]
    public async Task AConfirmedRotation_ToTheSameTarget_KeepsTheSampledValues_AndEnqueuesNoRescan()
    {
        var source = Source(disabled: false);
        var sampled = Sampled(out var world, source);
        var scheduler = new Mock<IBeaconScheduler>(MockBehavior.Strict);

        await world.Service(_audit, scheduler: scheduler.Object).UpdateDataSource(Edit(RotatedConnection, confirm: true), CancellationToken.None);

        source.EncryptedConnectionData.Should().Be(Encrypted(RotatedConnection));
        sampled.SampleValues.Should().Be("[\"PAID\"]");
        sampled.SampleValuesComplete.Should().BeTrue();
        _audit.Single().DetailsJson.Should().Contain("\"targetChanged\":false");
    }

    [Test]
    public async Task AConfirmedTargetChange_KeepsTheStoredTables_AndEnqueuesAMetadataRescan()
    {
        var source = Source(disabled: false);
        var sampled = Sampled(out var world, source);
        var scheduler = new Mock<IBeaconScheduler>();
        scheduler.Setup(x => x.EnqueueMetadataRefresh(SourceId)).ReturnsAsync("job-1");

        await world.Service(_audit, scheduler: scheduler.Object).UpdateDataSource(Edit(MovedConnection, confirm: true), CancellationToken.None);

        world.Columns.Should().ContainSingle().Which.Should().BeSameAs(sampled, "no metadata row is deleted");
        sampled.DatabaseMetadata.TableName.Should().Be("orders");
        scheduler.Verify(x => x.EnqueueMetadataRefresh(SourceId), Times.Once);
    }

    [Test]
    public async Task AConfirmedTargetChange_OfASourceThatLoadsNoMetadata_EnqueuesNoRescan()
    {
        var source = Source(disabled: false);
        source.MetadataLoadingEnabled = false;
        var world = new World(source);
        var scheduler = new Mock<IBeaconScheduler>(MockBehavior.Strict);

        await world.Service(_audit, scheduler: scheduler.Object).UpdateDataSource(Edit(MovedConnection, confirm: true), CancellationToken.None);

        source.EncryptedConnectionData.Should().Be(Encrypted(MovedConnection));
    }

    [Test]
    public async Task AConfirmedTargetChange_OnAHostThatRunsNoRescan_SavesAndLogsThatTheOldSchemaStays()
    {
        var logs = new LogRecorder();
        var source = Source(disabled: false);
        var world = new World(source);

        await world.Service(_audit, logger: logs.For<DataSourceService>(), scheduler: new HostWithoutJobs())
            .UpdateDataSource(Edit(MovedConnection, confirm: true), CancellationToken.None);

        source.EncryptedConnectionData.Should().Be(Encrypted(MovedConnection));
        logs.Entries.Should().Contain(x => x.Level == LogLevel.Warning && x.Message.Contains("No metadata refresh was enqueued", StringComparison.Ordinal));
        logs.Entries.Should().NotContain(x => x.FullText.Contains("db-b.internal", StringComparison.Ordinal));
    }

    [Test]
    public async Task AnEngineChange_EmptiesTheOldEnginesPool_NotTheNewOnes()
    {
        var postgres = RecordPoolClears(DatabaseEngineType.PostgreSQL);
        var sqlServer = RecordPoolClears(DatabaseEngineType.MSSQL);
        var source = Source(disabled: false);
        var world = new World(source);

        await world.Service(_audit).UpdateDataSource(
            Edit("Server=db-a.internal;Database=sales;User Id=reader;Password=new-pass;Encrypt=Strict", engine: DatabaseEngineType.MSSQL, confirm: true),
            CancellationToken.None);

        source.DatabaseEngineType.Should().Be(DatabaseEngineType.MSSQL);
        postgres.Should().Equal(OldConnection);
        sqlServer.Should().BeEmpty();
    }

    [Test]
    public async Task APoolThatCannotBeCleared_DoesNotFailTheSavedUpdate_AndIsLoggedByType()
    {
        DbConnectionFactory.Register(
            DatabaseEngineType.PostgreSQL,
            cs => new RecordingDbConnection { ConnectionString = cs },
            _ => throw new InvalidOperationException("pool of " + OldConnection));
        var logs = new LogRecorder();
        var source = Source(disabled: false);
        var world = new World(source);

        await world.Service(_audit, logger: logs.For<DataSourceService>()).UpdateDataSource(Edit(RotatedConnection), CancellationToken.None);

        source.EncryptedConnectionData.Should().Be(Encrypted(RotatedConnection));
        world.Saves.Should().Be(1);
        var warning = logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning && x.Message.Contains("could not be cleared", StringComparison.Ordinal)).Subject;
        warning.Message.Should().Contain(nameof(InvalidOperationException));
        warning.FullText.Should().NotContain("old-pass");
    }

    [Test]
    public async Task AnEngineWithoutAPoolClearer_IsLogged_ByIdAndEngineOnly()
    {
        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, cs => new RecordingDbConnection { ConnectionString = cs });
        var logs = new LogRecorder();
        var world = new World(Source(disabled: false));

        await world.Service(_audit, logger: logs.For<DataSourceService>()).SetDataSourceEnabled(SourceId, enabled: false, AdaId, CancellationToken.None);

        var warning = logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning && x.Message.Contains("no pool clearer", StringComparison.Ordinal)).Subject;
        warning.Message.Should().Contain("12").And.Contain("PostgreSQL").And.NotContain("db-a.internal").And.NotContain("old-pass");
    }

    [Test]
    public async Task ANameOnlyEdit_ClearsNoPool_AndANoOpEdit_RecordsNothing()
    {
        var cleared = RecordPoolClears();
        var source = Source(disabled: false);
        var world = new World(source);

        await world.Service(_audit).UpdateDataSource(Edit(null, name: "warehouse-eu"), CancellationToken.None);
        await world.Service(_audit).UpdateDataSource(Edit(null, name: "warehouse-eu"), CancellationToken.None);
        await world.Service(_audit).UpdateDataSource(Edit(OldConnection, name: "warehouse-eu"), CancellationToken.None);

        cleared.Should().BeEmpty();
        _audit.Events.Select(x => x.Action).Should().Equal(SecurityAuditActions.DataSourceUpdated);
    }

    [Test]
    public async Task ADatabaseSourceWithoutAnEngine_IsRefused()
    {
        var source = Source(disabled: false);
        var world = new World(source);

        var act = () => world.Service(_audit).UpdateDataSource(Edit(RotatedConnection, engine: null, confirm: true), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*needs a database engine*");
        source.DatabaseEngineType.Should().Be(DatabaseEngineType.PostgreSQL);
        _audit.Events.Should().BeEmpty();
    }

    [Test]
    public async Task AnEngineChange_KeepingTheStoredConnection_IsRefused()
    {
        var source = Source(disabled: false);
        var world = new World(source);

        var act = () => world.Service(_audit).UpdateDataSource(Edit(null, engine: DatabaseEngineType.MySQL, confirm: true), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*connection entered again*");
        source.DatabaseEngineType.Should().Be(DatabaseEngineType.PostgreSQL);
        world.Saves.Should().Be(0);
    }

    [Test]
    public async Task MetadataOptionsLeftOut_KeepTheStoredValues_AndAnEmptySchemaListClearsOne()
    {
        var source = Source(disabled: false);
        source.MetadataLoadingEnabled = false;
        source.MetadataMaxTables = 50;
        source.MetadataMaxColumnsPerTable = 20;
        source.MetadataLoadTableNamesOnly = true;
        source.MetadataExcludeSchemas = "audit";
        source.MetadataIncludeSchemas = "sales,app";
        var world = new World(source);

        await world.Service(_audit).UpdateDataSource(Edit(null), CancellationToken.None);

        source.Should().BeEquivalentTo(
            new { MetadataLoadingEnabled = false, MetadataMaxTables = 50, MetadataMaxColumnsPerTable = 20, MetadataLoadTableNamesOnly = true, MetadataExcludeSchemas = "audit", MetadataIncludeSchemas = "sales,app" },
            x => x.ExcludingMissingMembers());
        _audit.Events.Should().BeEmpty("nothing changed");

        var clear = Edit(null);
        clear.MetadataExcludeSchemas = [];
        clear.MetadataMaxTables = 10;
        await world.Service(_audit).UpdateDataSource(clear, CancellationToken.None);

        source.MetadataExcludeSchemas.Should().BeNull();
        source.MetadataMaxTables.Should().Be(10);
        source.MetadataIncludeSchemas.Should().Be("sales,app");
        _audit.Single().DetailsJson.Should().Be("{\"connectionChanged\":false,\"targetChanged\":false,\"changedFields\":[\"MetadataMaxTables\",\"MetadataExcludeSchemas\"]}");
    }

    [Test]
    public async Task DisablingADisabledSource_KeepsWhoAndWhen_AndRecordsNothing()
    {
        var source = Source(disabled: true);
        var disabledAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        source.DisabledTime = disabledAt;
        source.DisabledByUserId = 3;
        var world = new World(source);

        await world.Service(_audit).SetDataSourceEnabled(SourceId, enabled: false, AdaId, CancellationToken.None);

        source.DisabledTime.Should().Be(disabledAt);
        source.DisabledByUserId.Should().Be(3);
        _audit.Events.Should().BeEmpty();
        world.Saves.Should().Be(0);
    }

    [Test]
    public async Task Disabling_EvictsTheCachedMetadataAndGraph()
    {
        var world = new World(Source(disabled: false));
        world.Cache.Set(DatabaseMetadataService.GetCacheKey(SourceId), "cached");

        await world.Service(_audit).SetDataSourceEnabled(SourceId, enabled: false, AdaId, CancellationToken.None);

        world.Cache.TryGetValue(DatabaseMetadataService.GetCacheKey(SourceId), out _).Should().BeFalse();
        world.Graph.Verify(x => x.Invalidate(SourceId), Times.Once);
    }

    [Test]
    public async Task AnUnknownSource_IsNotFound_ForAnEditAndForADisable()
    {
        var world = new World(Source(disabled: false));

        var edit = () => world.Service(_audit).UpdateDataSource(Edit(RotatedConnection, id: 99), CancellationToken.None);
        var disable = () => world.Service(_audit).SetDataSourceEnabled(99, enabled: false, AdaId, CancellationToken.None);

        await edit.Should().ThrowAsync<BeaconNotFoundException>();
        await disable.Should().ThrowAsync<BeaconNotFoundException>();
    }

    [Test]
    public async Task ARunWhoseSecondStepReadsADisabledSource_IsRecordedWithEverySourceItWouldHaveRead()
    {
        var store = Subscribed();
        store.Warehouse.DisabledTime = null;
        store.Ledger.DisabledTime = DateTime.UtcNow;
        store.Query.Steps.Add(new QueryStep
        {
            Id = 12,
            QueryId = GovernanceStore.QueryId,
            DataSourceId = GovernanceStore.LedgerId,
            DataSource = store.Ledger,
            StepOrder = 2,
            Name = "Step 2",
            SqlValue = "SELECT 2",
        });

        await Jobs(store, new Mock<IDataSourceConnectionResolver>(MockBehavior.Strict).Object, request: null).ExecuteQuery(SubscriptionId, CancellationToken.None);

        var run = store.Saved.OfType<QueryExecutionHistory>().Should().ContainSingle().Subject;
        run.NotificationStatus.Should().Be(NotificationStatus.Failed);
        run.Comment.Should().Be(NotificationFailureReasons.DataSourceDisabled);
        run.DataSourceIds.Should().Equal([GovernanceStore.DataSourceId, GovernanceStore.LedgerId]);
    }

    private static DataSource Source(bool disabled, bool allowMigrationWrites = false) => new()
    {
        Id = SourceId,
        Name = "warehouse",
        DataSourceType = DataSourceType.Database,
        DatabaseEngineType = DatabaseEngineType.PostgreSQL,
        EncryptedConnectionData = Encrypted(OldConnection),
        AllowMigrationWrites = allowMigrationWrites,
        DisabledTime = disabled ? DateTime.UtcNow : null,
        DisabledByUserId = disabled ? AdaId : null
    };

    private static Beacon.Core.Models.DataSources.DataSourceData Edit(string? connection, string name = "warehouse", DatabaseEngineType? engine = DatabaseEngineType.PostgreSQL, bool confirm = false, int id = SourceId) => new()
    {
        DataSourceId = id,
        Name = name,
        DatabaseEngineType = engine,
        ConnectionString = connection,
        ConfirmTargetChange = confirm
    };

    private static string Encrypted(string value) => "enc:" + value;

    private static IEncryptionService Encryption()
    {
        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(x => x.Encrypt(It.IsAny<string>())).Returns<string>(Encrypted);
        encryption.Setup(x => x.Decrypt(It.IsAny<string>())).Returns<string>(x => x.StartsWith("enc:", StringComparison.Ordinal)
            ? x["enc:".Length..]
            : throw new System.Security.Cryptography.CryptographicException("unreadable"));

        return encryption.Object;
    }

    private static List<string> RecordPoolClears(DatabaseEngineType engine = DatabaseEngineType.PostgreSQL)
    {
        var cleared = new List<string>();
        DbConnectionFactory.Register(engine, cs => new RecordingDbConnection { ConnectionString = cs }, x => cleared.Add(x.ConnectionString));

        return cleared;
    }

    private static ColumnMetadata Sampled(out World world, DataSource source)
    {
        var table = new DatabaseMetadata { DataSourceId = SourceId, SchemaName = "public", TableName = "orders" };
        var sampled = new ColumnMetadata { ColumnName = "status", DataType = "text", DatabaseMetadata = table, SampleValues = "[\"PAID\"]", SampleValuesComplete = true };
        world = new World(source);
        world.Columns.Add(sampled);

        return sampled;
    }

    private static BeaconContext Context(bool sqlServer)
    {
        return sqlServer ? SqlServerTestContext.Create() : NpgsqlTestContext.Create();
    }

    // The outer filter tests the disabled time, and only ever for its absence.
    private static void ShouldKeepOnlyEnabledSources(string sql, bool sqlServer)
    {
        var filter = sql[sql.LastIndexOf("\nWHERE ", StringComparison.Ordinal)..];
        var references = Regex.Matches(filter, sqlServer ? @"\[\w+\]\.\[DisabledTime\]" : @"\w+\.disabled_time").Count;
        var absent = Regex.Matches(filter, sqlServer ? @"\[\w+\]\.\[DisabledTime\] IS NULL" : @"\w+\.disabled_time IS NULL").Count;

        references.Should().BeGreaterThan(0, sql);
        absent.Should().Be(references, sql);
    }

    private static GovernanceStore Subscribed()
    {
        var store = new GovernanceStore();
        store.Warehouse.DisabledTime = DateTime.UtcNow;
        store.Subscriptions.Add(new Subscription
        {
            Id = SubscriptionId,
            QueryId = GovernanceStore.QueryId,
            Query = store.Query,
            CronExpression = "0 * * * *",
            NotificationTrigger = NotificationTrigger.Always,
        });

        return store;
    }

    private static JobService Jobs(GovernanceStore store, IDataSourceConnectionResolver connections, HttpContext? request)
    {
        var http = new HttpContextAccessor { HttpContext = request };

        return new JobService(
            store.Factory(),
            TestQueryServices.QueryService(store.Factory(), connections: connections, configuration: new BeaconConfiguration()),
            Mock.Of<INotificationService>(MockBehavior.Strict),
            Mock.Of<ITaskService>(MockBehavior.Strict),
            Mock.Of<IAnomalyDetectionService>(MockBehavior.Strict),
            Mock.Of<IDataQualityEvaluationService>(MockBehavior.Strict),
            NullLogger<JobService>.Instance,
            new RunAsScope(http),
            http,
            TestChangeApprovals.Deliveries());
    }

    private static void ShouldBeFailed(GovernanceStore store)
    {
        var run = store.Saved.OfType<QueryExecutionHistory>().Should().ContainSingle().Subject;
        run.NotificationStatus.Should().Be(NotificationStatus.Failed);
        run.Comment.Should().Be(NotificationFailureReasons.DataSourceDisabled);
        run.ResultCount.Should().Be(0);
        run.DataSourceIds.Should().Equal([GovernanceStore.DataSourceId]);
        NotificationFailureReasons.Displayable(run.Comment).Should().Be(NotificationFailureReasons.DataSourceDisabled, "readers of the history see the reason");
    }

    private sealed class World
    {
        private readonly List<object> _saved = [];
        private readonly List<DataSource> _sources;

        public World(DataSource source)
        {
            _sources = [source];
        }

        public List<ColumnMetadata> Columns { get; } = [];

        public MemoryCache Cache { get; } = new(new MemoryCacheOptions());

        public Mock<ISchemaGraphService> Graph { get; } = new();

        public int Saves { get; private set; }

        public DataSourceService Service(
            RecordingSecurityAudit audit,
            IDataSourceTransportPolicy? transport = null,
            ILogger<DataSourceService>? logger = null,
            IBeaconScheduler? scheduler = null)
        {
            var sets = new Dictionary<Type, object>
            {
                [typeof(DataSource)] = RecordingBeaconContext.MemorySet(_sources, _saved),
                [typeof(ColumnMetadata)] = RecordingBeaconContext.MemorySet(Columns, _saved)
            };
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new RecordingBeaconContext(sets, _saved, onSave: () => Saves++));

            return new DataSourceService(
                factory.Object,
                Encryption(),
                Mock.Of<IDataSourceProviderFactory>(),
                Mock.Of<IManualQueryExecutionLogger>(),
                Mock.Of<IBeaconUserContext>(),
                Cache,
                Graph.Object,
                TestQueryServices.Runner(),
                audit,
                transport ?? TestTransport.Policy(),
                logger ?? NullLogger<DataSourceService>.Instance,
                scheduler: scheduler);
        }
    }

    // A host scheduler that keeps the interface's defaults: it runs no metadata re-scan.
    private sealed class HostWithoutJobs : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
