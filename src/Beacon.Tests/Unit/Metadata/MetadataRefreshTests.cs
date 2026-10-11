using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.DataSources;
using Beacon.Core.Models;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.Metadata;

/// <summary>
/// A forced metadata refresh: one per data source per cooldown, curated descriptions kept over the catalog's, and the
/// cached metadata and relationship graph rebuilt after it; archiving or changing a data source evicts both caches. The
/// services run over in-memory sets (no database, §4.7).
/// </summary>
[TestFixture]
public class MetadataRefreshTests
{
    private const int DataSourceId = 7;

    [Test]
    public async Task ForcedRefresh_WithinTheCooldown_IsRefusedWithoutScanning()
    {
        var metadata = new Mock<IDatabaseMetadataService>();
        var handler = RefreshHandler(metadata, cooldownMinutes: 15);

        await handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);
        var again = () => handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);

        await again.Should().ThrowAsync<BeaconRateLimitedException>().WithMessage("*less than 15 minutes ago*");
        metadata.Verify(x => x.RefreshMetadataAsync(DataSourceId, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ForcedRefresh_CooldownIsPerDataSource()
    {
        var metadata = new Mock<IDatabaseMetadataService>();
        var handler = RefreshHandler(metadata, cooldownMinutes: 15);

        await handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);
        await handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId + 1), CancellationToken.None);

        metadata.Verify(x => x.RefreshMetadataAsync(It.IsAny<int>(), true, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task ForcedRefresh_CooldownZero_IsNotLimited()
    {
        var metadata = new Mock<IDatabaseMetadataService>();
        var handler = RefreshHandler(metadata, cooldownMinutes: 0);

        await handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);
        await handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);

        metadata.Verify(x => x.RefreshMetadataAsync(DataSourceId, true, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task ForcedRefresh_ThatFailed_DoesNotHoldBackTheRetry()
    {
        var metadata = new Mock<IDatabaseMetadataService>();
        var handler = RefreshHandler(metadata, cooldownMinutes: 15);
        metadata
            .SetupSequence(x => x.RefreshMetadataAsync(DataSourceId, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The data source could not be reached."))
            .ReturnsAsync(Snapshot());

        var failed = () => handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);
        await failed.Should().ThrowAsync<InvalidOperationException>();
        await handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);

        metadata.Verify(x => x.RefreshMetadataAsync(DataSourceId, true, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task ForcedRefresh_AfterTheCooldownSinceTheLastSuccess_IsAccepted()
    {
        var metadata = new Mock<IDatabaseMetadataService>();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var handler = RefreshHandler(metadata, cooldownMinutes: 15, time: time);

        await handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(16));
        await handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);

        metadata.Verify(x => x.RefreshMetadataAsync(DataSourceId, true, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task ForcedRefresh_ALoweredCooldown_AppliesToTheNextRequest()
    {
        var metadata = new Mock<IDatabaseMetadataService>();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var settings = new RefreshSettings { CurrentValue = new MetadataRefreshOptions { ForcedRefreshCooldownMinutes = 60 } };
        var handler = RefreshHandler(metadata, settings: settings, time: time);

        await handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(6));
        settings.CurrentValue = new MetadataRefreshOptions { ForcedRefreshCooldownMinutes = 5 };
        await handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);

        metadata.Verify(x => x.RefreshMetadataAsync(DataSourceId, true, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task ForcedRefresh_WhileOneOfTheSameSourceRuns_IsRefused()
    {
        var metadata = new Mock<IDatabaseMetadataService>();
        var handler = RefreshHandler(metadata, cooldownMinutes: 15);
        var release = new TaskCompletionSource<DatabaseMetadataSnapshot>();
        metadata
            .Setup(x => x.RefreshMetadataAsync(DataSourceId, true, It.IsAny<CancellationToken>()))
            .Returns(release.Task);

        var running = handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);
        var second = () => handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);

        await second.Should().ThrowAsync<BeaconRateLimitedException>().WithMessage(ForcedMetadataRefreshCooldown.AlreadyRunningMessage);
        release.SetResult(Snapshot());
        await running;
        metadata.Verify(x => x.RefreshMetadataAsync(DataSourceId, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ChangingADataSource_LetsItsMetadataBeRefreshedAgainAtOnce()
    {
        var world = new MetadataWorld();
        var metadata = new Mock<IDatabaseMetadataService>();
        var handler = RefreshHandler(metadata, cooldownMinutes: 15, cache: world.Cache);

        await handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);
        await world.DataSourceService().UpdateDataSource(
            new Beacon.Core.Models.DataSources.DataSourceData
            {
                DataSourceId = DataSourceId,
                Name = "warehouse",
                ConnectionString = "Host=fixed",
                DatabaseEngineType = DatabaseEngineType.PostgreSQL
            },
            CancellationToken.None);
        await handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);

        metadata.Verify(x => x.RefreshMetadataAsync(DataSourceId, true, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestCase(-1, false)]
    [TestCase(0, true)]
    [TestCase(15, true)]
    [TestCase(MetadataRefreshOptions.MaxCooldownMinutes, true)]
    [TestCase(MetadataRefreshOptions.MaxCooldownMinutes + 1, false)]
    public void CooldownOption_OutOfRange_FailsValidation(int minutes, bool valid)
    {
        var result = new MetadataRefreshOptionsValidator()
            .Validate(null, new MetadataRefreshOptions { ForcedRefreshCooldownMinutes = minutes });

        result.Succeeded.Should().Be(valid);
    }

    [Test]
    public void CooldownOption_DefaultsToFifteenMinutes()
    {
        new MetadataRefreshOptions().ForcedRefreshCooldownMinutes.Should().Be(15);
    }

    [Test]
    public async Task Refresh_KeepsCuratedDescriptions_AndTakesTheCatalogsForTheRest()
    {
        var world = new MetadataWorld();
        world.Stored.Add(Table("sales", "orders", "Curated: one row per order", ("status", "Curated: lifecycle state"), ("total", "old catalog comment")));
        world.Stored.Add(Table("sales", "refunds", "old catalog text", ("id", null)));
        world.Patches.Add(AppliedPatch(McpDocPatchTarget.TableDescription, "sales.orders"));
        world.Patches.Add(AppliedPatch(McpDocPatchTarget.ColumnDescription, "sales.orders.status"));
        world.Patches.Add(new McpDocumentationPatch
        {
            DataSourceId = DataSourceId,
            TargetType = McpDocPatchTarget.TableDescription,
            TargetIdentifier = "sales.refunds",
            ProposedContent = "never applied",
            Reasoning = "proposed only",
            Status = McpDocPatchStatus.Proposed
        });
        world.Extracted =
        [
            Extracted("sales", "orders", "catalog: orders", ("status", "catalog: status"), ("total", "catalog: total")),
            Extracted("sales", "refunds", null, ("id", "catalog: id"))
        ];

        var snapshot = await world.Service().RefreshMetadataAsync(DataSourceId, forceRefresh: true);

        var orders = world.Saved.OfType<DatabaseMetadata>().Single(x => x.TableName == "orders");
        orders.TableDescription.Should().Be("Curated: one row per order");
        orders.Columns.Single(x => x.ColumnName == "status").Description.Should().Be("Curated: lifecycle state");
        orders.Columns.Single(x => x.ColumnName == "total").Description.Should().Be("catalog: total", "a description no patch curated follows the catalog");
        var refunds = world.Saved.OfType<DatabaseMetadata>().Single(x => x.TableName == "refunds");
        refunds.TableDescription.Should().BeNull("only an applied patch curates a description");
        refunds.Columns.Single().Description.Should().Be("catalog: id");
        snapshot.Tables.Single(x => x.TableName == "orders").Description.Should().Be("Curated: one row per order", "the cached snapshot is what was stored");
    }

    [Test]
    public async Task Refresh_InvalidatesTheRelationshipGraph()
    {
        var world = new MetadataWorld();
        world.Extracted = [Extracted("sales", "orders", null, ("id", null))];

        await world.Service().RefreshMetadataAsync(DataSourceId, forceRefresh: true);

        world.Graph.Verify(x => x.Invalidate(DataSourceId), Times.Once);
    }

    [Test]
    public async Task MetadataRead_OfADataSourceTheDatabaseDoesNotReturn_IsNotFound()
    {
        var world = new MetadataWorld();
        world.DataSources.Clear();

        var act = () => world.Service().GetMetadataAsync(DataSourceId);

        await act.Should().ThrowAsync<BeaconNotFoundException>();
    }

    [Test]
    public async Task ArchivingADataSource_EvictsItsMetadataAndGraph()
    {
        var world = new MetadataWorld();
        world.Cache.Set(DatabaseMetadataService.GetCacheKey(DataSourceId), Snapshot());

        await world.DataSourceService().DeleteDataSource(DataSourceId, CancellationToken.None);

        world.DataSources.Single().ArchivedTime.Should().NotBeNull();
        world.Cache.TryGetValue(DatabaseMetadataService.GetCacheKey(DataSourceId), out _).Should().BeFalse("an archived source's metadata is never served from the cache");
        world.Graph.Verify(x => x.Invalidate(DataSourceId), Times.Once);
    }

    [Test]
    public async Task ChangingADataSourcesConnection_EvictsItsMetadataAndGraph()
    {
        var world = new MetadataWorld();
        world.Cache.Set(DatabaseMetadataService.GetCacheKey(DataSourceId), Snapshot());

        await world.DataSourceService().UpdateDataSource(
            new Beacon.Core.Models.DataSources.DataSourceData
            {
                DataSourceId = DataSourceId,
                Name = "warehouse",
                ConnectionString = "Host=elsewhere",
                DatabaseEngineType = DatabaseEngineType.PostgreSQL
            },
            CancellationToken.None);

        world.Cache.TryGetValue(DatabaseMetadataService.GetCacheKey(DataSourceId), out _).Should().BeFalse();
        world.Graph.Verify(x => x.Invalidate(DataSourceId), Times.Once);
    }

    private static RefreshDataSourceMetadataHandler RefreshHandler(
        Mock<IDatabaseMetadataService> metadata,
        int cooldownMinutes = 15,
        RefreshSettings? settings = null,
        MemoryCache? cache = null,
        TimeProvider? time = null)
    {
        metadata
            .Setup(x => x.RefreshMetadataAsync(It.IsAny<int>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot());

        return new RefreshDataSourceMetadataHandler(
            metadata.Object,
            Mock.Of<ISchemaRelationshipSyncService>(),
            new ForcedMetadataRefreshCooldown(cache ?? new MemoryCache(new MemoryCacheOptions()), time ?? TimeProvider.System),
            settings ?? new RefreshSettings { CurrentValue = new MetadataRefreshOptions { ForcedRefreshCooldownMinutes = cooldownMinutes } },
            NullLogger<RefreshDataSourceMetadataHandler>.Instance);
    }

    private static DatabaseMetadataSnapshot Snapshot()
    {
        return new DatabaseMetadataSnapshot(DataSourceId, DatabaseEngineType.PostgreSQL, [], DateTime.UtcNow);
    }

    private static DatabaseMetadata Table(string schema, string table, string? description, params (string Name, string? Description)[] columns)
    {
        return new DatabaseMetadata
        {
            DataSourceId = DataSourceId,
            SchemaName = schema,
            TableName = table,
            TableDescription = description,
            Columns = columns
                .Select(x => new ColumnMetadata { ColumnName = x.Name, DataType = "text", Description = x.Description })
                .ToList()
        };
    }

    private static TableMetadataDto Extracted(string schema, string table, string? description, params (string Name, string? Description)[] columns)
    {
        return new TableMetadataDto(
            schema,
            table,
            columns
                .Select((x, i) => new ColumnMetadataDto(x.Name, "text", true, false, false, i + 1, null, null, null, null, x.Description))
                .ToList(),
            [],
            description);
    }

    private static McpDocumentationPatch AppliedPatch(McpDocPatchTarget target, string identifier)
    {
        return new McpDocumentationPatch
        {
            DataSourceId = DataSourceId,
            TargetType = target,
            TargetIdentifier = identifier,
            ProposedContent = "applied",
            Reasoning = "curated",
            Status = McpDocPatchStatus.Applied
        };
    }

    /// <summary>The refresh settings as the host's configuration holds them now; a test changes them between requests.</summary>
    private sealed class RefreshSettings : IOptionsMonitor<MetadataRefreshOptions>
    {
        public MetadataRefreshOptions CurrentValue { get; set; } = new();

        public MetadataRefreshOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<MetadataRefreshOptions, string?> listener) => null;
    }

    /// <summary>One PostgreSQL data source, its stored metadata and patches, over in-memory sets.</summary>
    private sealed class MetadataWorld
    {
        public List<DataSource> DataSources { get; } =
        [
            new DataSource
            {
                Id = DataSourceId,
                Name = "warehouse",
                DataSourceType = DataSourceType.Database,
                EncryptedConnectionData = "encrypted",
                DatabaseEngineType = DatabaseEngineType.PostgreSQL
            }
        ];

        public List<DatabaseMetadata> Stored { get; } = [];

        public List<McpDocumentationPatch> Patches { get; } = [];

        public IReadOnlyList<TableMetadataDto> Extracted { get; set; } = [];

        public List<object> Saved { get; } = [];

        public MemoryCache Cache { get; } = new(new MemoryCacheOptions());

        public Mock<ISchemaGraphService> Graph { get; } = new();

        public DatabaseMetadataService Service()
        {
            var extractor = new Mock<IDatabaseMetadataExtractor>();
            extractor.SetupGet(x => x.SupportedEngineType).Returns(DatabaseEngineType.PostgreSQL);
            extractor
                .Setup(x => x.ExtractMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Extracted);

            return new DatabaseMetadataService(
                Factory(),
                Encryption(),
                [extractor.Object],
                Mock.Of<IColumnValueSampler>(),
                SettingsProviderMock.Unshared(SettingsProviderMock.Create(new McpSettingsData { EnableSampleValueCollection = false }).Object),
                Cache,
                Graph.Object,
                NullLogger<DatabaseMetadataService>.Instance);
        }

        public IDataSourceService DataSourceService()
        {
            return new DataSourceService(
                Factory(),
                Encryption(),
                Mock.Of<IDataSourceProviderFactory>(),
                Mock.Of<IManualQueryExecutionLogger>(),
                Mock.Of<IBeaconUserContext>(),
                Cache,
                Graph.Object,
                NullLogger<DataSourceService>.Instance);
        }

        private IDbContextFactory<BeaconContext> Factory()
        {
            var sets = new Dictionary<Type, object>
            {
                [typeof(DataSource)] = RecordingBeaconContext.MemorySet(DataSources, Saved),
                [typeof(DatabaseMetadata)] = RecordingBeaconContext.MemorySet(Stored, Saved),
                [typeof(McpDocumentationPatch)] = RecordingBeaconContext.MemorySet(Patches, Saved),
                [typeof(Beacon.Core.Data.Entities.DataMigration.MigrationJob)] = RecordingBeaconContext.MemorySet(new List<Beacon.Core.Data.Entities.DataMigration.MigrationJob>(), Saved)
            };
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new RecordingBeaconContext(sets, Saved));

            return factory.Object;
        }

        private static IEncryptionService Encryption()
        {
            var encryption = new Mock<IEncryptionService>();
            encryption.Setup(x => x.Decrypt(It.IsAny<string>())).Returns("Host=unused");
            encryption.Setup(x => x.Encrypt(It.IsAny<string>())).Returns("encrypted");

            return encryption.Object;
        }
    }
}
