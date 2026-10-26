using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.DataMigration;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.Models;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Models.Providers;
using Beacon.Core.RowSecurity;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Beacon.Tests.Common;

/// <summary>
/// The real metadata service and column value sampler for tests, without a database (§4.7): their dependencies are
/// in-memory sets or doubles one level down.
/// </summary>
internal static class TestMetadataServices
{
    /// <summary>
    /// A metadata service the test must never reach: its contexts, connection resolver, settings, graph and catalog
    /// snapshots are strict doubles, so any read or scan fails.
    /// </summary>
    public static DatabaseMetadataService Unused()
    {
        return new DatabaseMetadataService(
            Mock.Of<IDbContextFactory<BeaconContext>>(MockBehavior.Strict),
            Mock.Of<IDataSourceConnectionResolver>(MockBehavior.Strict),
            [],
            Sampler(),
            Mock.Of<ISharedSourceSettings>(MockBehavior.Strict),
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<ISchemaGraphService>(MockBehavior.Strict),
            NullLogger<DatabaseMetadataService>.Instance,
            Mock.Of<ICatalogSnapshotCache>(MockBehavior.Strict));
    }

    /// <summary>
    /// A metadata service that serves <paramref name="snapshot"/> as the cached catalog of its data source, a readable
    /// (enabled, not a write target) database source; it never scans.
    /// </summary>
    public static DatabaseMetadataService Serving(DatabaseMetadataSnapshot snapshot)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set(DatabaseMetadataService.GetCacheKey(snapshot.DataSourceId), snapshot);
        var source = new DataSource
        {
            Id = snapshot.DataSourceId,
            Name = "source",
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = snapshot.DatabaseEngineType,
            EncryptedConnectionData = "encrypted"
        };

        return new DatabaseMetadataService(
            Contexts([source]),
            Mock.Of<IDataSourceConnectionResolver>(MockBehavior.Strict),
            [],
            Sampler(),
            Mock.Of<ISharedSourceSettings>(MockBehavior.Strict),
            cache,
            Mock.Of<ISchemaGraphService>(),
            NullLogger<DatabaseMetadataService>.Instance,
            Mock.Of<ICatalogSnapshotCache>());
    }

    /// <summary>
    /// A metadata service over <paramref name="dataSources"/>, read as the test fills them, that never scans: a source
    /// with metadata loading off serves an empty catalog.
    /// </summary>
    public static DatabaseMetadataService Reading(List<DataSource> dataSources)
    {
        return new DatabaseMetadataService(
            Contexts(dataSources),
            Mock.Of<IDataSourceConnectionResolver>(MockBehavior.Strict),
            [],
            Sampler(),
            Mock.Of<ISharedSourceSettings>(MockBehavior.Strict),
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<ISchemaGraphService>(),
            NullLogger<DatabaseMetadataService>.Instance,
            Mock.Of<ICatalogSnapshotCache>());
    }

    /// <summary>
    /// A metadata service whose catalog scan of <paramref name="dataSource"/>, the one data source it knows, returns
    /// what <paramref name="extract"/> does (which may count, wait or throw); sample collection is off.
    /// </summary>
    public static DatabaseMetadataService Scanning(DataSource dataSource, Func<Task<IReadOnlyList<TableMetadataDto>>> extract)
    {
        var extractor = new Mock<IDatabaseMetadataExtractor>();
        extractor.SetupGet(x => x.SupportedEngineType).Returns(dataSource.DatabaseEngineType ?? DatabaseEngineType.PostgreSQL);
        extractor
            .Setup(x => x.ExtractMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(extract);
        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(x => x.Decrypt(It.IsAny<string>())).Returns("Host=unused");

        return new DatabaseMetadataService(
            Contexts([dataSource]),
            TestTransport.Resolver(encryption.Object),
            [extractor.Object],
            Sampler(),
            SettingsProviderMock.Unshared(SettingsProviderMock.Create(new McpSettingsData { EnableSampleValueCollection = false }).Object),
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<ISchemaGraphService>(),
            NullLogger<DatabaseMetadataService>.Instance,
            Mock.Of<ICatalogSnapshotCache>());
    }

    /// <summary>
    /// The column value sampler over the real gate, reading through <paramref name="providers"/> (default: a strict
    /// factory, so a sampling statement never runs) under <paramref name="rowSecurity"/> (default: row security off).
    /// </summary>
    public static ColumnValueSampler Sampler(IDataSourceProviderFactory? providers = null, IRowSecurityResolver? rowSecurity = null)
    {
        return new ColumnValueSampler(
            new QueryGuardrailService(),
            new GatedSqlRunner(TestSqlGate.Create(), providers ?? Mock.Of<IDataSourceProviderFactory>(MockBehavior.Strict), TestConfiguredDatabases.None, TestQueryServices.QueryOptions),
            rowSecurity ?? TestRowSecurity.Resolver,
            NullLogger<ColumnValueSampler>.Instance);
    }

    private static IDbContextFactory<BeaconContext> Contexts(List<DataSource> dataSources)
    {
        var saved = new List<object>();
        var sets = new Dictionary<Type, object>
        {
            [typeof(DataSource)] = RecordingBeaconContext.MemorySet(dataSources, saved),
            [typeof(DatabaseMetadata)] = RecordingBeaconContext.MemorySet(new List<DatabaseMetadata>(), saved),
            [typeof(ColumnMetadata)] = RecordingBeaconContext.MemorySet(new List<ColumnMetadata>(), saved),
            [typeof(McpDocumentationPatch)] = RecordingBeaconContext.MemorySet(new List<McpDocumentationPatch>(), saved),
            [typeof(MigrationJob)] = RecordingBeaconContext.MemorySet(new List<MigrationJob>(), saved)
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, saved));

        return factory.Object;
    }
}
