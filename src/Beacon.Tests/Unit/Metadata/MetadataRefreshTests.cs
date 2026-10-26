using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.DataSources;
using Beacon.Core.Models;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;
using Beacon.Core.SecurityAudit;

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
        var world = new MetadataWorld();
        var handler = RefreshHandler(world, cooldownMinutes: 15);

        await handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);
        var again = () => handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);

        await again.Should().ThrowAsync<BeaconRateLimitedException>().WithMessage("*less than 15 minutes ago*");
        world.Scans.Should().Be(1);
    }

    [Test]
    public async Task AForcedRefresh_IsRecorded_WithWhetherItSkippedTheCooldown_AndARefusedOneIsNot()
    {
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(4, "ext-dsa"));
        var world = new MetadataWorld();
        var handler = RefreshHandler(world, cooldownMinutes: 15, audit: audit);

        await handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);
        var again = () => handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);
        await again.Should().ThrowAsync<BeaconRateLimitedException>();

        var refreshed = audit.Single("ext-dsa");
        refreshed.Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.MetadataRefreshed, ActorUserId = (int?)4, ResourceType = SecurityAuditResources.DataSource, ResourceId = "7" },
            x => x.ExcludingMissingMembers());
        refreshed.DetailsJson.Should().Be("{\"forced\":true,\"cooldownExempt\":false}");
        audit.Ways.Should().Equal("recorded");

        var exempt = new RecordingSecurityAudit();
        await RefreshHandler(new MetadataWorld(), permissions: TestPermissions.Holding(BeaconPermissions.DataSourceAdmin), audit: exempt)
            .Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);
        exempt.Single().DetailsJson.Should().Be("{\"forced\":true,\"cooldownExempt\":true}");
    }

    [Test]
    public async Task AFailedForcedRefresh_IsNotRecorded()
    {
        var audit = new RecordingSecurityAudit();
        var world = new MetadataWorld();
        var handler = RefreshHandler(world, audit: audit);
        world.BeforeExtract = () => throw new InvalidOperationException("scan failed");

        var act = () => handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        audit.Events.Should().BeEmpty();
    }

    [Test]
    public async Task ForcedRefresh_CooldownIsPerDataSource()
    {
        var world = new MetadataWorld();
        world.DataSources.Add(new DataSource
        {
            Id = DataSourceId + 1,
            Name = "lake",
            DataSourceType = DataSourceType.Database,
            EncryptedConnectionData = "encrypted",
            DatabaseEngineType = DatabaseEngineType.PostgreSQL
        });
        var handler = RefreshHandler(world, cooldownMinutes: 15);

        await handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);
        await handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId + 1), CancellationToken.None);

        world.Scans.Should().Be(2);
    }

    [Test]
    public async Task ForcedRefresh_CooldownZero_IsNotLimited()
    {
        var world = new MetadataWorld();
        var handler = RefreshHandler(world, cooldownMinutes: 0);

        await handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);
        await handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);

        world.Scans.Should().Be(2);
    }

    [Test]
    public async Task ForcedRefresh_ThatFailed_DoesNotHoldBackTheRetry()
    {
        var world = new MetadataWorld();
        var handler = RefreshHandler(world, cooldownMinutes: 15);
        var failures = 1;
        world.BeforeExtract = () => failures-- > 0 ? throw new InvalidOperationException("The data source could not be reached.") : Task.CompletedTask;

        var failed = () => handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);
        await failed.Should().ThrowAsync<InvalidOperationException>();
        await handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);

        world.Scans.Should().Be(2);
    }

    [Test]
    public async Task ForcedRefresh_AfterTheCooldownSinceTheLastSuccess_IsAccepted()
    {
        var world = new MetadataWorld();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var handler = RefreshHandler(world, cooldownMinutes: 15, time: time);

        await handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(16));
        await handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);

        world.Scans.Should().Be(2);
    }

    [Test]
    public async Task ForcedRefresh_ALoweredCooldown_AppliesToTheNextRequest()
    {
        var world = new MetadataWorld();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var settings = new RefreshSettings { CurrentValue = new MetadataRefreshOptions { ForcedRefreshCooldownMinutes = 60 } };
        var handler = RefreshHandler(world, settings: settings, time: time);

        await handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(6));
        settings.CurrentValue = new MetadataRefreshOptions { ForcedRefreshCooldownMinutes = 5 };
        await handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);

        world.Scans.Should().Be(2);
    }

    [Test]
    public async Task ForcedRefresh_WhileOneOfTheSameSourceRuns_IsRefused()
    {
        var world = new MetadataWorld();
        var handler = RefreshHandler(world, cooldownMinutes: 15);
        var release = new TaskCompletionSource();
        world.BeforeExtract = () => release.Task;

        var running = handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);
        var second = () => handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);

        await second.Should().ThrowAsync<BeaconRateLimitedException>().WithMessage(ForcedMetadataRefreshCooldown.AlreadyRunningMessage);
        release.SetResult();
        await running;
        world.Scans.Should().Be(1);
    }

    [Test]
    public async Task ADataSourceAdmin_IsNotHeldToTheCooldown()
    {
        var world = new MetadataWorld();
        var handler = RefreshHandler(world, cooldownMinutes: 15, permissions: TestPermissions.Holding(BeaconPermissions.DataSourceAdmin));

        await handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);
        await handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);

        world.Scans.Should().Be(2);
    }

    [Test]
    public async Task ADataSourceAdmin_StillWaitsForARefreshOfTheSameSourceThatRuns()
    {
        var world = new MetadataWorld();
        var handler = RefreshHandler(world, cooldownMinutes: 15, permissions: TestPermissions.Holding(BeaconPermissions.DataSourceAdmin));
        var release = new TaskCompletionSource();
        world.BeforeExtract = () => release.Task;

        var running = handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);
        var second = () => handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);

        await second.Should().ThrowAsync<BeaconRateLimitedException>().WithMessage(ForcedMetadataRefreshCooldown.AlreadyRunningMessage);
        release.SetResult();
        await running;
    }

    [Test]
    public async Task ChangingADataSource_LetsItsMetadataBeRefreshedAgainAtOnce()
    {
        var world = new MetadataWorld();
        var handler = RefreshHandler(world, cooldownMinutes: 15, cache: world.Cache);

        await handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);
        await world.DataSourceService().UpdateDataSource(
            new Beacon.Core.Models.DataSources.DataSourceData
            {
                DataSourceId = DataSourceId,
                Name = "warehouse",
                ConnectionString = "Host=fixed;Password=reader;SSL Mode=VerifyFull",
                DatabaseEngineType = DatabaseEngineType.PostgreSQL,
                ConfirmTargetChange = true
            },
            CancellationToken.None);
        await handler.Handle(new RefreshDataSourceMetadataCommand(DataSourceId), CancellationToken.None);

        world.Scans.Should().Be(2);
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

    // Beacon builds SQL from catalog names, so a name that could end its quoting in some dialect is never stored.
    [Test]
    public async Task Refresh_LeavesOutNamesCarryingQuotesBracketsSemicolonsOrControlCharacters()
    {
        var world = new MetadataWorld();
        world.Extracted =
        [
            Extracted("sales", "orders", null, ("id", null), ("status';SELECT 1;--", null), ("a]b", null), ("tab\tname", null), ("Order Date", null)),
            Extracted("sales", "x\"y", null, ("id", null)),
            Extracted("pg`temp", "events", null, ("id", null))
        ];

        var logs = new LogRecorder();

        var snapshot = await world.Service(logs.For<DatabaseMetadataService>()).RefreshMetadataAsync(DataSourceId, forceRefresh: true);

        var stored = world.Saved.OfType<DatabaseMetadata>().ToList();
        stored.Should().ContainSingle().Which.TableName.Should().Be("orders");
        stored[0].Columns.Select(x => x.ColumnName).Should().BeEquivalentTo(["id", "Order Date"]);
        snapshot.SkippedNameCount.Should().Be(5, "two tables and three columns were left out");
        var warning = logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning).Subject;
        warning.Message.Should().Contain("sales.x\"y").And.Contain("sales.orders.a]b");
        warning.Message.Should().Contain("sales.orders.tab\\u0009name", "a control character is written as an escape, never raw");
        warning.Message.Should().NotContain("\t");
    }

    // A refresh replaces the stored catalog: a column the sampler now leaves out — a secret-like name, a credential-shaped
    // value — is stored without the samples it held before; an ordinary column gets its new ones. Nothing else is scrubbed.
    [Test]
    public async Task Refresh_StoresNoSamplesForAColumnTheSamplerLeavesOut_ClearingWhatItHeld()
    {
        var world = new MetadataWorld();
        var old = new DatabaseMetadata { Id = 1, DataSourceId = DataSourceId, SchemaName = "sales", TableName = "partners" };
        old.Columns =
        [
            new ColumnMetadata { ColumnName = "api_key", DataType = "text", DatabaseMetadata = old, SampleValues = "[\"held-before\"]" },
            new ColumnMetadata { ColumnName = "reference", DataType = "text", DatabaseMetadata = old, SampleValues = "[\"held-before\"]" },
            new ColumnMetadata { ColumnName = "status", DataType = "text", DatabaseMetadata = old, SampleValues = "[\"OLD\"]" }
        ];
        world.Stored.Add(old);
        world.Extracted = [Extracted("sales", "partners", null, ("api_key", null), ("reference", null), ("status", null))];
        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DataSource _, string sql, Dictionary<string, object?> _, CancellationToken _) => new ProviderQueryResult
            {
                Success = true,
                Rows = !sql.StartsWith("SELECT DISTINCT", StringComparison.Ordinal)
                    ? [new Dictionary<string, object?> { ["api_key"] = "partner-key-1", ["reference"] = "ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8", ["status"] = "ACTIVE" }]
                    : sql.Contains("\"reference\"", StringComparison.Ordinal)
                        ? [new Dictionary<string, object?> { ["reference"] = "ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8" }]
                        : [new Dictionary<string, object?> { ["status"] = "ACTIVE" }]
            });
        var providers = new Mock<IDataSourceProviderFactory>();
        providers
            .Setup(x => x.GetProvider(It.IsAny<DataSourceType>()))
            .Returns(provider.Object);
        var sampler = new ColumnValueSampler(
            new QueryGuardrailService(),
            new GatedSqlRunner(TestSqlGate.Create(), providers.Object, TestConfiguredDatabases.None, TestQueryServices.QueryOptions),
            TestRowSecurity.Resolver,
            NullLogger<ColumnValueSampler>.Instance);

        await world.Service(sampler: sampler, settings: new McpSettingsData { EnableSampleValueCollection = true }).RefreshMetadataAsync(DataSourceId, forceRefresh: true);

        var stored = world.Saved.OfType<DatabaseMetadata>().Single().Columns;
        stored.Single(x => x.ColumnName == "api_key").SampleValues.Should().BeNull("a secret-like name is never sampled");
        stored.Single(x => x.ColumnName == "reference").SampleValues.Should().BeNull("a credential-shaped value drops the column's samples");
        stored.Single(x => x.ColumnName == "status").SampleValues.Should().Be("[\"ACTIVE\"]");
        provider.Invocations
            .Select(x => (string)x.Arguments[1])
            .Should().NotContain(x => x.Contains("DISTINCT \"api_key\"", StringComparison.Ordinal), "no domain probe reads it either");
    }

    [Test]
    public async Task Refresh_InvalidatesTheRelationshipGraphAndTheCatalogSnapshots()
    {
        var world = new MetadataWorld();
        world.Extracted = [Extracted("sales", "orders", null, ("id", null))];

        await world.Service().RefreshMetadataAsync(DataSourceId, forceRefresh: true);

        world.Graph.Verify(x => x.Invalidate(DataSourceId), Times.Once);
        world.Snapshots.Verify(x => x.Invalidate(DataSourceId), Times.Once);
    }

    // The stored catalog changed: what was built from it is dropped even when a later step of the refresh fails.
    [Test]
    public async Task Refresh_ThatFailsAfterStoringTheCatalog_StillInvalidatesTheCatalogSnapshots()
    {
        var world = new MetadataWorld();
        world.Extracted = [Extracted("sales", "orders", null, ("id", null))];
        world.BeforeContext = () =>
        {
            if (world.Saved.Count > 0)
            {
                throw new InvalidOperationException("database unavailable");
            }
        };

        var refresh = () => world.Service().RefreshMetadataAsync(DataSourceId, forceRefresh: true);

        await refresh.Should().ThrowAsync<InvalidOperationException>();
        world.Snapshots.Verify(x => x.Invalidate(DataSourceId), Times.Once);
        world.Graph.Verify(x => x.Invalidate(DataSourceId), Times.Once);
    }

    // A disabled source keeps its stored tables and sampled values for when it is enabled, but none of them is served.
    [TestCase(false)]
    [TestCase(true)]
    public async Task MetadataRead_OfADisabledSource_ServesNoStoredTableOrSampledValue(bool disabled)
    {
        var world = new MetadataWorld();
        world.DataSources.Single().DisabledTime = disabled ? DateTime.UtcNow : null;
        var table = new DatabaseMetadata { DataSourceId = DataSourceId, SchemaName = "sales", TableName = "orders", LastRefreshed = DateTime.UtcNow };
        table.Columns.Add(new ColumnMetadata { ColumnName = "status", DataType = "text", DatabaseMetadata = table, SampleValues = "[\"PAID\"]" });
        world.Stored.Add(table);

        var catalog = await world.Service().GetMetadataAsync(DataSourceId);

        if (disabled)
        {
            catalog.Tables.Should().BeEmpty("a disabled source's catalog is not served");
            world.Cache.TryGetValue(DatabaseMetadataService.GetCacheKey(DataSourceId), out _).Should().BeFalse("the empty catalog is not cached");

            return;
        }

        catalog.Tables.Should().ContainSingle().Which.Columns.Should().ContainSingle().Which.SampleValues.Should().Equal("PAID");
    }

    // An API source's stored endpoints are not served while it is disabled either: the state is checked before any
    // source-specific loader.
    [Test]
    public async Task MetadataRead_OfADisabledApiSource_ServesNoStoredEndpoint()
    {
        var world = new MetadataWorld();
        var source = world.DataSources.Single();
        source.DataSourceType = DataSourceType.Api;
        source.DatabaseEngineType = null;
        source.DisabledTime = DateTime.UtcNow;
        var endpoint = new DatabaseMetadata { DataSourceId = DataSourceId, SchemaName = "api", TableName = "GET /orders", LastRefreshed = DateTime.UtcNow };
        endpoint.Columns.Add(new ColumnMetadata { ColumnName = "status", DataType = "string", DatabaseMetadata = endpoint });
        world.Stored.Add(endpoint);

        var catalog = await world.Service().GetMetadataAsync(DataSourceId);

        catalog.Tables.Should().BeEmpty("a disabled API source's endpoints are not served");
        catalog.DatabaseEngineType.Should().BeNull();
        world.Cache.TryGetValue(DatabaseMetadataService.GetCacheKey(DataSourceId), out _).Should().BeFalse("the empty catalog is not cached");
    }

    // A snapshot cached before the source was disabled or marked as a write target — by this instance or another, whose
    // eviction never reaches this cache — is not served once the database says so, on the refresh's cached path neither.
    [TestCase(false)]
    [TestCase(true)]
    public async Task ACachedCatalog_OfASourceDisabledOrMarkedElsewhere_IsNotServed(bool writeTarget)
    {
        var world = new MetadataWorld();
        var source = world.DataSources.Single();
        source.AllowMigrationWrites = writeTarget;
        source.DisabledTime = writeTarget ? null : DateTime.UtcNow;
        var key = DatabaseMetadataService.GetCacheKey(DataSourceId);
        world.Cache.Set(key, CachedCatalog());
        var service = world.Service();

        var catalog = await service.GetMetadataAsync(DataSourceId);

        catalog.Tables.Should().BeEmpty("the cached catalog outlived the change");
        world.Cache.TryGetValue(key, out _).Should().BeFalse("the stale entry is evicted");

        world.Cache.Set(key, CachedCatalog());
        var refresh = () => service.RefreshMetadataAsync(DataSourceId, forceRefresh: false);

        if (writeTarget)
        {
            await refresh.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage(MigrationWriteTargets.ReadRefusedMessage);
        }
        else
        {
            await refresh.Should().ThrowAsync<DataSourceDisabledException>();
        }

        world.Cache.TryGetValue(key, out _).Should().BeFalse();
    }

    // While the workflow covers data-source connections, a created source waiting for its first approval is scanned through
    // the resolver, which refuses it: it has no catalog until it is approved.
    [Test]
    public async Task TheScanOfAHeldSource_IsRefused_AndPublishesNoCatalog()
    {
        var world = new MetadataWorld();
        world.DataSources.Single().ApprovedTargetHash = DataSourceApprovalDefinition.Held;
        var configuration = new BeaconConfiguration();
        configuration.ApprovalWorkflow.Enabled = true;
        configuration.ApprovalWorkflow.CoversDataSourceConnections = true;

        var refresh = () => world.Service(configuration: configuration).RefreshMetadataAsync(DataSourceId, forceRefresh: true);

        await refresh.Should().ThrowAsync<DataSourceAwaitingApprovalException>();
        world.Cache.TryGetValue(DatabaseMetadataService.GetCacheKey(DataSourceId), out _).Should().BeFalse();
        world.Stored.Should().BeEmpty();
    }

    [Test]
    public async Task ACachedCatalog_OfAnEnabledSource_IsServed()
    {
        var world = new MetadataWorld();
        var cached = CachedCatalog();
        world.Cache.Set(DatabaseMetadataService.GetCacheKey(DataSourceId), cached);

        var catalog = await world.Service().GetMetadataAsync(DataSourceId);

        catalog.Should().BeSameAs(cached);
    }

    // A disable that lands while a refresh is scanning: the refresh publishes nothing, returns no catalog, and the next
    // read is empty.
    [Test]
    public async Task ARefresh_ThatADisableOvertakes_PublishesNothing_AndTheNextReadIsEmpty()
    {
        var world = new MetadataWorld();
        world.Extracted = [Extracted("sales", "orders", null, ("id", null))];
        var scanning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.BeforeExtract = async () =>
        {
            scanning.SetResult();
            await release.Task;
        };
        var service = world.Service();

        var refresh = service.RefreshMetadataAsync(DataSourceId, forceRefresh: true);
        await scanning.Task;
        world.DataSources.Single().DisabledTime = DateTime.UtcNow;
        release.SetResult();
        var refreshed = await refresh;

        refreshed.Tables.Should().BeEmpty();
        world.Cache.TryGetValue(DatabaseMetadataService.GetCacheKey(DataSourceId), out _).Should().BeFalse("the overtaken scan is not published");
        (await service.GetMetadataAsync(DataSourceId)).Tables.Should().BeEmpty();
    }

    // A connection change that lands while a refresh is scanning: the old connection's scan is not cached.
    [Test]
    public async Task ARefresh_ThatAConnectionChangeOvertakes_IsNotCached()
    {
        var world = new MetadataWorld();
        world.Extracted = [Extracted("sales", "orders", null, ("id", null))];
        world.BeforeExtract = () =>
        {
            world.DataSources.Single().EncryptedConnectionData = "re-encrypted";

            return Task.CompletedTask;
        };

        var refreshed = await world.Service().RefreshMetadataAsync(DataSourceId, forceRefresh: true);

        refreshed.Tables.Should().ContainSingle();
        world.Cache.TryGetValue(DatabaseMetadataService.GetCacheKey(DataSourceId), out _).Should().BeFalse();
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
    public async Task ArchivingADataSource_EvictsItsMetadataGraphAndCatalogSnapshots()
    {
        var world = new MetadataWorld();
        world.Cache.Set(DatabaseMetadataService.GetCacheKey(DataSourceId), Snapshot());

        await world.DataSourceService().DeleteDataSource(DataSourceId, CancellationToken.None);

        world.DataSources.Single().ArchivedTime.Should().NotBeNull();
        world.Cache.TryGetValue(DatabaseMetadataService.GetCacheKey(DataSourceId), out _).Should().BeFalse("an archived source's metadata is never served from the cache");
        world.Graph.Verify(x => x.Invalidate(DataSourceId), Times.Once);
        world.Snapshots.Verify(x => x.Invalidate(DataSourceId), Times.Once);
    }

    // Archiving takes the column values sampled from the source with it, archived tables' columns included; another
    // source's samples stay. Nothing already archived is scrubbed: only this archive clears.
    [Test]
    public async Task ArchivingADataSource_ClearsTheColumnValuesSampledFromIt()
    {
        var world = new MetadataWorld();
        var table = new DatabaseMetadata { DataSourceId = DataSourceId, SchemaName = "public", TableName = "orders" };
        var archivedTable = new DatabaseMetadata { DataSourceId = DataSourceId, SchemaName = "public", TableName = "old_orders", ArchivedTime = DateTime.UtcNow };
        var otherTable = new DatabaseMetadata { DataSourceId = DataSourceId + 1, SchemaName = "public", TableName = "orders" };
        var sampled = new ColumnMetadata { ColumnName = "status", DataType = "text", DatabaseMetadata = table, SampleValues = "[\"PAID\"]", SampleValuesComplete = true };
        var sampledArchived = new ColumnMetadata { ColumnName = "status", DataType = "text", DatabaseMetadata = archivedTable, SampleValues = "[\"OPEN\"]" };
        var otherSource = new ColumnMetadata { ColumnName = "status", DataType = "text", DatabaseMetadata = otherTable, SampleValues = "[\"OPEN\"]", SampleValuesComplete = true };
        world.StoredColumns.AddRange([sampled, sampledArchived, otherSource]);

        await world.DataSourceService().DeleteDataSource(DataSourceId, CancellationToken.None);

        world.DataSources.Single().ArchivedTime.Should().NotBeNull();
        sampled.SampleValues.Should().BeNull();
        sampled.SampleValuesComplete.Should().BeFalse();
        sampledArchived.SampleValues.Should().BeNull("an archived table of the source keeps no copy either");
        otherSource.SampleValues.Should().Be("[\"OPEN\"]");
        otherSource.SampleValuesComplete.Should().BeTrue();
    }

    [Test]
    public async Task ChangingADataSourcesConnection_EvictsItsMetadataGraphAndCatalogSnapshots()
    {
        var world = new MetadataWorld();
        world.Cache.Set(DatabaseMetadataService.GetCacheKey(DataSourceId), Snapshot());

        await world.DataSourceService().UpdateDataSource(
            new Beacon.Core.Models.DataSources.DataSourceData
            {
                DataSourceId = DataSourceId,
                Name = "warehouse",
                ConnectionString = "Host=elsewhere;Password=reader;SSL Mode=VerifyFull",
                DatabaseEngineType = DatabaseEngineType.PostgreSQL,
                ConfirmTargetChange = true
            },
            CancellationToken.None);

        world.Cache.TryGetValue(DatabaseMetadataService.GetCacheKey(DataSourceId), out _).Should().BeFalse();
        world.Graph.Verify(x => x.Invalidate(DataSourceId), Times.Once);
        world.Snapshots.Verify(x => x.Invalidate(DataSourceId), Times.Once);
    }

    private static RefreshDataSourceMetadataHandler RefreshHandler(
        MetadataWorld world,
        int cooldownMinutes = 15,
        RefreshSettings? settings = null,
        MemoryCache? cache = null,
        TimeProvider? time = null,
        IBeaconPermissionAccessor? permissions = null,
        ISecurityAuditRecorder? audit = null)
    {
        return new RefreshDataSourceMetadataHandler(
            new ForcedMetadataRefresh(world.Service(), Mock.Of<ISchemaRelationshipSyncService>(), NullLogger<ForcedMetadataRefresh>.Instance),
            new ForcedMetadataRefreshCooldown(cache ?? new MemoryCache(new MemoryCacheOptions()), time ?? TimeProvider.System),
            settings ?? new RefreshSettings { CurrentValue = new MetadataRefreshOptions { ForcedRefreshCooldownMinutes = cooldownMinutes } },
            permissions ?? TestPermissions.None,
            new MetadataSampleView(permissions ?? TestPermissions.None, Mock.Of<ISharedSourceSettings>(), new QueryGuardrailService()),
            audit ?? new RecordingSecurityAudit());
    }

    private static DatabaseMetadataSnapshot CachedCatalog()
    {
        return new DatabaseMetadataSnapshot(DataSourceId, DatabaseEngineType.PostgreSQL, [Extracted("sales", "orders", null, ("id", null))], DateTime.UtcNow);
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

        public List<ColumnMetadata> StoredColumns { get; } = [];

        public List<McpDocumentationPatch> Patches { get; } = [];

        public IReadOnlyList<TableMetadataDto> Extracted { get; set; } = [];

        /// <summary>Runs before every context the service opens.</summary>
        public Action? BeforeContext { get; set; }

        /// <summary>Runs as the scan starts, before the extracted tables are returned.</summary>
        public Func<Task>? BeforeExtract { get; set; }

        public List<object> Saved { get; } = [];

        /// <summary>How many catalog scans its services ran.</summary>
        public int Scans { get; private set; }

        public MemoryCache Cache { get; } = new(new MemoryCacheOptions());

        public Mock<ISchemaGraphService> Graph { get; } = new();

        public Mock<ICatalogSnapshotCache> Snapshots { get; } = new();

        public DatabaseMetadataService Service(
            ILogger<DatabaseMetadataService>? logger = null,
            ColumnValueSampler? sampler = null,
            McpSettingsData? settings = null,
            BeaconConfiguration? configuration = null)
        {
            var extractor = new Mock<IDatabaseMetadataExtractor>();
            extractor.SetupGet(x => x.SupportedEngineType).Returns(DatabaseEngineType.PostgreSQL);
            extractor
                .Setup(x => x.ExtractMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(async () =>
                {
                    Scans++;
                    if (BeforeExtract != null)
                    {
                        await BeforeExtract();
                    }

                    return Extracted;
                });

            return new DatabaseMetadataService(
                Factory(),
                TestTransport.Resolver(Encryption(), configuration: configuration),
                [extractor.Object],
                sampler ?? TestMetadataServices.Sampler(),
                SettingsProviderMock.Unshared(SettingsProviderMock.Create(settings ?? new McpSettingsData { EnableSampleValueCollection = false }).Object),
                Cache,
                Graph.Object,
                logger ?? NullLogger<DatabaseMetadataService>.Instance,
                Snapshots.Object);
        }

        public DataSourceService DataSourceService()
        {
            return new DataSourceService(
                Factory(),
                Encryption(),
                TestProviderFactory.Accepting(),
                Cache,
                Graph.Object,
                new RecordingSecurityAudit(),
                TestTransport.Policy(),
                TestEngineVerifier.Passing,
                TestChangeApprovals.Submitter(),
                TestTransport.Hasher(),
                NullLogger<DataSourceService>.Instance,
                catalogSnapshots: Snapshots.Object);
        }

        private IDbContextFactory<BeaconContext> Factory()
        {
            var sets = new Dictionary<Type, object>
            {
                [typeof(DataSource)] = RecordingBeaconContext.MemorySet(DataSources, Saved),
                [typeof(DatabaseMetadata)] = RecordingBeaconContext.MemorySet(Stored, Saved),
                [typeof(ColumnMetadata)] = RecordingBeaconContext.MemorySet(StoredColumns, Saved),
                [typeof(McpDocumentationPatch)] = RecordingBeaconContext.MemorySet(Patches, Saved),
                [typeof(Beacon.Core.Data.Entities.DataMigration.MigrationJob)] = RecordingBeaconContext.MemorySet(new List<Beacon.Core.Data.Entities.DataMigration.MigrationJob>(), Saved)
            };
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    BeforeContext?.Invoke();

                    return new RecordingBeaconContext(sets, Saved);
                });

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
