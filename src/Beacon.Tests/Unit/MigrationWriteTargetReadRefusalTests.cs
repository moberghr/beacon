using Beacon.Connector.MySql;
using Beacon.Connector.PostgreSql;
using Beacon.Connector.SqlServer;
using Beacon.Core;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Exceptions;
using Beacon.Core.Helpers;
using Beacon.Core.HostData;
using Beacon.Core.HostDocs;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Providers;
using Beacon.MCP.Tools;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// Beacon never reads a migration write target: the one place a data source's credentials become a connection string for
/// a read refuses it, so a query step on one fails before anything connects; the metadata service offers no catalog for
/// it and scans none; and the project listings MCP and AI read leave it out. Recording doubles, no database (§4.7).
/// </summary>
[TestFixture]
[NonParallelizable] // DbConnectionFactory is a process-wide registry.
public class MigrationWriteTargetReadRefusalTests
{
    private const int ProjectId = 4;
    private const int ReadSource = 10;
    private const int WriteTarget = 20;

    [TearDown]
    public void RestoreConnectionFactories()
    {
        new BeaconBuilder(new ServiceCollection(), new ConfigurationBuilder().Build())
            .AddPostgreSqlConnector()
            .AddSqlServerConnector()
            .AddMySqlConnector();
    }

    [Test]
    public void TheConnectionResolver_RefusesAWriteTarget_AndResolvesAReadSource()
    {
        var resolver = new DataSourceConnectionResolver(Encryption(), new ConfigurationBuilder().Build(), TestTransport.Policy(), new BeaconConfiguration(), TestTransport.Hasher());

        var read = resolver.GetConnectionString(Source(ReadSource));
        var write = () => resolver.GetConnectionString(Source(WriteTarget, writeTarget: true));

        read.Should().Be("Host=unused;Database=unused");
        write.Should().Throw<InvalidOperationException>().WithMessage(MigrationWriteTargets.ReadRefusedMessage);
    }

    [Test]
    public async Task AQueryStepOnAWriteTarget_IsRefused_BeforeAnythingConnects()
    {
        var connection = new RecordingDbConnection { RowCount = 3 };
        var connections = 0;
        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, _ =>
        {
            connections++;
            return connection;
        });
        var service = TestQueryServices.QueryService(
            QueryOn(Source(WriteTarget, writeTarget: true)),
            connections: new DataSourceConnectionResolver(Encryption(), new ConfigurationBuilder().Build(), TestTransport.Policy(), new BeaconConfiguration(), TestTransport.Hasher()));

        var act = () => service.ExecuteQueryAdvanced(7, cancellationToken: CancellationToken.None);

        await act.Should().ThrowAsync<QueryExecutionFailedException>().WithMessage(MigrationWriteTargets.ReadRefusedMessage);
        connections.Should().Be(0, "nothing connects with a write target's credentials to read");
        connection.ExecutedCommands.Should().BeEmpty();
    }

    [Test]
    public async Task TheMetadataService_OffersNoCatalogForAWriteTarget_AndScansNone()
    {
        var target = Source(WriteTarget, writeTarget: true);
        var world = new ProjectAccessWorld();
        world.DataSources.Add(target);
        var extractor = new Mock<IDatabaseMetadataExtractor>();
        extractor.SetupGet(x => x.SupportedEngineType).Returns(DatabaseEngineType.PostgreSQL);
        var service = new DatabaseMetadataService(
            world.Factory(),
            TestTransport.Resolver(Encryption()),
            [extractor.Object],
            TestMetadataServices.Sampler(),
            Mock.Of<ISharedSourceSettings>(),
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<ISchemaGraphService>(),
            NullLogger<DatabaseMetadataService>.Instance);

        var catalog = await service.GetMetadataAsync(WriteTarget, CancellationToken.None);
        var scan = () => service.RefreshMetadataAsync(WriteTarget, forceRefresh: true, CancellationToken.None);

        catalog.Tables.Should().BeEmpty("a write target has no catalog in Beacon");
        await scan.Should().ThrowAsync<InvalidOperationException>().WithMessage(MigrationWriteTargets.ReadRefusedMessage);
        extractor.Verify(x => x.ExtractMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task TheProjectListingsMcpReads_LeaveAWriteTargetOut()
    {
        var world = new ProjectAccessWorld();
        world.Project(ProjectId);
        world.Source(ReadSource, ProjectId);
        world.Source(WriteTarget, ProjectId).AllowMigrationWrites = true;
        var factory = world.Factory();

        var ids = await ToolHelper.GetProjectDataSourceIdsAsync(factory, ProjectId, CancellationToken.None);
        var readSourceRefusal = await ToolHelper.ValidateDataSourceInProjectAsync(factory, ProjectId, ReadSource, CancellationToken.None);
        var writeTargetRefusal = await ToolHelper.ValidateDataSourceInProjectAsync(factory, ProjectId, WriteTarget, CancellationToken.None);
        var byName = await ToolHelper.ResolveDataSourceByNameAsync(factory, ProjectId, $"source {WriteTarget}", CancellationToken.None);

        ids.Should().Equal(ReadSource);
        readSourceRefusal.Should().BeNull("a read source is part of the project for MCP");
        writeTargetRefusal.Should().NotBeNull("a write target is not part of the project for MCP");
        byName.Error.Should().NotBeNull();
    }

    // The links kept are those whose data source is NOT a write target: the predicate's polarity, on both providers.
    [TestCase(false)]
    [TestCase(true)]
    public void WhereReadable_KeepsOnlyLinksToSourcesThatAreNotWriteTargets_Translates(bool sqlServer)
    {
        using var context = Context(sqlServer);

        var sql = context.ProjectDataSources
            .WhereReadable()
            .Where(x => x.ProjectId == ProjectId)
            .Select(x => x.DataSourceId)
            .ToQueryString();

        ShouldKeepOnlyReadSources(sql, sqlServer);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TheProjectBriefsDataSources_LeaveWriteTargetsOut_Translates(bool sqlServer)
    {
        using var context = Context(sqlServer);

        var sql = ProjectBriefService.BuildDataSourcesQuery(context, ProjectId).ToQueryString();

        ShouldKeepOnlyReadSources(sql, sqlServer);
    }

    // The data-source list's migration-targets filter: true lists only write targets (a destination picker), false only the
    // sources Beacon reads (a source picker), unset both — the value travels as a parameter, on both providers.
    [TestCase(false, true)]
    [TestCase(false, false)]
    [TestCase(true, true)]
    [TestCase(true, false)]
    public async Task TheDataSourceListsMigrationTargetsFilter_Translates(bool sqlServer, bool targets)
    {
        var column = sqlServer ? "[AllowMigrationWrites] = @" : "allow_migration_writes = @";

        var filtered = await new ListHandlerHarness(TestProjectAccess.Unrestricted, sqlServer)
            .CaptureAsync(new Beacon.Core.Handlers.DataSources.GetDataSourcesQuery { MigrationTargets = targets });
        var unfiltered = await new ListHandlerHarness(TestProjectAccess.Unrestricted, sqlServer)
            .SendAsync(new Beacon.Core.Handlers.DataSources.GetDataSourcesQuery());

        var index = filtered.Commands
            .Select((x, i) => (Sql: x, Index: i))
            .Where(x => x.Sql.Contains(column, StringComparison.Ordinal))
            .Select(x => x.Index)
            .First();
        var sql = filtered.Commands[index];
        var start = sql.IndexOf(column, StringComparison.Ordinal) + column.Length - 1;
        var parameter = new string(sql[start..].TakeWhile(x => x == '@' || char.IsLetterOrDigit(x) || x == '_').ToArray());
        filtered.CommandParameters[index][parameter].Should().Be(targets);
        unfiltered.Should().NotContain(x => x.Contains(column, StringComparison.Ordinal), "no filter is applied when the request leaves it unset");
    }

    // The queries, subscriptions and data contracts counted before a source becomes a write target: live rows reading it.
    [TestCase(false)]
    [TestCase(true)]
    public void WhatReadsADataSource_Translates(bool sqlServer)
    {
        using var context = Context(sqlServer);

        var queries = context.Queries.WhereReading(WriteTarget).Select(x => x.Id).ToQueryString();
        var subscriptions = context.Subscriptions.WhereReading(WriteTarget).Select(x => x.Id).ToQueryString();
        var contracts = context.DataContracts.WhereReading(WriteTarget).Select(x => x.Id).ToQueryString();

        var sourceColumn = sqlServer ? "[DataSourceId]" : "data_source_id";
        var archived = sqlServer ? "[ArchivedTime] IS NULL" : "archived_time IS NULL";
        foreach (var sql in new[] { queries, subscriptions, contracts })
        {
            sql.Should().Contain(sourceColumn).And.Contain(archived, "archived rows read nothing");
            sql.Should().NotContain("WHERE FALSE").And.NotContain("0 = 1").And.NotContain("1 = 0");
        }

        queries.Should().Contain("EXISTS");
        subscriptions.Should().Contain("EXISTS");
    }

    private static Beacon.Core.Data.BeaconContext Context(bool sqlServer)
    {
        return sqlServer ? SqlServerTestContext.Create() : NpgsqlTestContext.Create();
    }

    // The outer filter tests the flag, and only ever negated: write targets are what is left out.
    private static void ShouldKeepOnlyReadSources(string sql, bool sqlServer)
    {
        var filter = sql[sql.LastIndexOf("\nWHERE ", StringComparison.Ordinal)..];
        var references = System.Text.RegularExpressions.Regex.Matches(filter, sqlServer ? @"\[\w+\]\.\[AllowMigrationWrites\]" : @"\w+\.allow_migration_writes").Count;
        var negated = System.Text.RegularExpressions.Regex.Matches(filter, sqlServer ? @"\[\w+\]\.\[AllowMigrationWrites\] = CAST\(0 AS bit\)" : @"NOT \(\w+\.allow_migration_writes\)").Count;

        references.Should().BeGreaterThan(0, sql);
        negated.Should().Be(references, sql);
    }

    private static IEncryptionService Encryption()
    {
        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(x => x.Decrypt(It.IsAny<string>())).Returns("Host=unused;Database=unused");

        return encryption.Object;
    }

    private static DataSource Source(int id, bool writeTarget = false)
    {
        return new DataSource
        {
            Id = id,
            Name = $"source {id}",
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = DatabaseEngineType.PostgreSQL,
            EncryptedConnectionData = "encrypted",
            AllowMigrationWrites = writeTarget
        };
    }

    private static IDbContextFactory<BeaconContext> QueryOn(DataSource dataSource)
    {
        var query = new Query { Id = 7, Name = "copy" };
        var step = new QueryStep
        {
            Id = 1,
            QueryId = query.Id,
            Query = query,
            StepOrder = 1,
            DataSourceId = dataSource.Id,
            DataSource = dataSource,
            SqlValue = "SELECT id FROM accounts"
        };
        query.Steps.Add(step);
        var saved = new List<object>();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object>
                {
                    [typeof(DataSource)] = RecordingBeaconContext.MemorySet([dataSource], saved),
                    [typeof(Query)] = RecordingBeaconContext.MemorySet([query], saved),
                    [typeof(QueryStep)] = RecordingBeaconContext.MemorySet([step], saved)
                },
                saved));

        return factory.Object;
    }
}
