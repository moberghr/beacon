using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.DataSources;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Core.SecurityAudit;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Marking a data source as a migration write target: only while the deployment runs migrations (409 otherwise; returning
/// a target to a read source always works), only a PostgreSQL, MySQL or SQL Server database that is neither host-managed
/// nor marked read-only, and every change is audited — a settings-history row with the value before and after and the
/// Admin's id, and a security log line with ids only. The cached catalog of the source is dropped.
/// </summary>
[TestFixture]
public class SetDataSourceMigrationWritesHandlerTests
{
    private const int SourceId = 20;

    private ProjectAccessWorld _world = null!;
    private DataSource _source = null!;
    private LogRecorder _logs = null!;
    private RecordingSecurityAudit _audit = null!;
    private MemoryCache _cache = null!;
    private Mock<ISchemaGraphService> _graphs = null!;
    private List<ColumnMetadata> _columns = null!;

    [SetUp]
    public void SetUp()
    {
        _audit = new RecordingSecurityAudit();
        _world = new ProjectAccessWorld();
        _source = _world.Source(SourceId);
        _columns = [];
        _world.Serve(_columns);
        _world.Serve(new List<Query>());
        _world.Serve(new List<Subscription>());
        _world.Serve(new List<DataContract>());
        _world.Users.Add(new BeaconUser { Id = 1, ExternalId = "ext-root", UserName = "root", IsEnabled = true, IsSuperAdmin = true });
        _logs = new LogRecorder();
        _cache = new MemoryCache(new MemoryCacheOptions());
        _graphs = new Mock<ISchemaGraphService>();
    }

    [TearDown]
    public void TearDown()
    {
        _cache.Dispose();
    }

    [Test]
    public async Task MarkingASource_SetsTheFlag_AndRecordsWhoChangedWhatInTheHistoryAndTheSecurityLog()
    {
        _cache.Set(DatabaseMetadataService.GetCacheKey(SourceId), "cached catalog");

        await SendAsync(new SetDataSourceMigrationWritesCommand(SourceId, true));

        _source.AllowMigrationWrites.Should().BeTrue();
        _world.Saved.OfType<AppSettingHistory>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                SettingKey = $"DataSource.{SourceId}.AllowMigrationWrites",
                OldValue = "false",
                NewValue = "true",
                ChangedByUserId = "1"
            });
        _logs.Contains($"Data source {SourceId} migration writes changed from False to True by 1").Should().BeTrue();
        _audit.Single().Should().BeEquivalentTo(
            new
            {
                Action = SecurityAuditActions.MigrationWritesChanged,
                ResourceType = SecurityAuditResources.DataSource,
                ResourceId = SourceId.ToString(),
                DetailsJson = "{\"allowMigrationWritesBefore\":false,\"allowMigrationWritesAfter\":true,\"forced\":false}"
            },
            x => x.ExcludingMissingMembers());
        _audit.Ways.Should().Equal("staged");
        _cache.TryGetValue(DatabaseMetadataService.GetCacheKey(SourceId), out _).Should().BeFalse();
        _graphs.Verify(x => x.Invalidate(SourceId), Times.Once);
    }

    // Column values Beacon sampled through the source's old login go when it becomes a write target; another source's stay.
    [Test]
    public async Task MarkingASource_ClearsTheColumnValuesSampledFromIt()
    {
        var table = new DatabaseMetadata { Id = 1, DataSourceId = SourceId, SchemaName = "sales", TableName = "orders" };
        var otherTable = new DatabaseMetadata { Id = 2, DataSourceId = SourceId + 1, SchemaName = "sales", TableName = "orders" };
        var sampled = new ColumnMetadata { ColumnName = "status", DataType = "text", DatabaseMetadata = table, SampleValues = "[\"PAID\"]", SampleValuesComplete = true };
        var unsampled = new ColumnMetadata { ColumnName = "id", DataType = "int", DatabaseMetadata = table };
        var otherSource = new ColumnMetadata { ColumnName = "status", DataType = "text", DatabaseMetadata = otherTable, SampleValues = "[\"OPEN\"]", SampleValuesComplete = true };
        _columns.AddRange([sampled, unsampled, otherSource]);

        await SendAsync(new SetDataSourceMigrationWritesCommand(SourceId, true));

        sampled.SampleValues.Should().BeNull();
        sampled.SampleValuesComplete.Should().BeFalse();
        unsampled.SampleValues.Should().BeNull();
        otherSource.SampleValues.Should().Be("[\"OPEN\"]");
        otherSource.SampleValuesComplete.Should().BeTrue();
    }

    [Test]
    public async Task WhileTheDeploymentDoesNotRunMigrations_NoSourceIsMarked_ButATargetCanBeReturned()
    {
        var mark = () => SendAsync(new SetDataSourceMigrationWritesCommand(SourceId, true), enabled: false);

        await mark.Should().ThrowAsync<BeaconConflictException>().WithMessage(MigrationWriteTargets.DisabledMessage);
        _source.AllowMigrationWrites.Should().BeFalse();
        _world.Saved.Should().BeEmpty();

        _source.AllowMigrationWrites = true;
        await SendAsync(new SetDataSourceMigrationWritesCommand(SourceId, false), enabled: false);

        _source.AllowMigrationWrites.Should().BeFalse();
        _world.Saved.OfType<AppSettingHistory>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { OldValue = "true", NewValue = "false" });
    }

    [TestCase("host", false, DataSourceType.Database, DatabaseEngineType.PostgreSQL, "*host data source*")]
    [TestCase(null, true, DataSourceType.Database, DatabaseEngineType.PostgreSQL, "*marked read-only*")]
    [TestCase(null, false, DataSourceType.Database, DatabaseEngineType.Snowflake, "*PostgreSQL, MySQL or SQL Server*")]
    [TestCase(null, false, DataSourceType.BigQuery, null, "*PostgreSQL, MySQL or SQL Server*")]
    public async Task ASourceAMigrationCannotWrite_IsNotMarked(string? hostManagedKey, bool isReadOnly, DataSourceType type, DatabaseEngineType? engine, string message)
    {
        _source.HostManagedKey = hostManagedKey;
        _source.IsReadOnly = isReadOnly;
        _source.DataSourceType = type;
        _source.DatabaseEngineType = engine;

        var act = () => SendAsync(new SetDataSourceMigrationWritesCommand(SourceId, true));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(message);
        _source.AllowMigrationWrites.Should().BeFalse();
        _world.Saved.Should().BeEmpty();
    }

    [Test]
    public async Task AnUnknownSource_IsNotFound_AndAnUnchangedValueRecordsNothing()
    {
        var unknown = () => SendAsync(new SetDataSourceMigrationWritesCommand(999, true));

        await unknown.Should().ThrowAsync<BeaconNotFoundException>();
        await SendAsync(new SetDataSourceMigrationWritesCommand(SourceId, false));

        _world.Saved.Should().BeEmpty();
        _logs.Entries.Should().BeEmpty();
        _audit.Events.Should().BeEmpty();
    }

    // Beacon stops reading a write target, so what reads the source fails from then on: marking it needs the Admin's
    // confirmation, and the refusal says how many queries, subscriptions and data contracts read it (others' do not count).
    [Test]
    public async Task ASourceThatQueriesSubscriptionsAndContractsRead_IsNotMarkedUnconfirmed_AndTheRefusalCountsThem()
    {
        ServeWhatReads(SourceId);

        var act = () => SendAsync(new SetDataSourceMigrationWritesCommand(SourceId, true));

        await act.Should().ThrowAsync<BeaconConflictException>()
            .WithMessage($"Data source {SourceId} is read by 2 queries, 1 subscription and 1 data contract.*Confirm to mark it anyway.");
        _source.AllowMigrationWrites.Should().BeFalse();
        _world.Saved.Should().BeEmpty();
        _logs.Entries.Should().BeEmpty();
    }

    [Test]
    public async Task AConfirmedMark_OfASourceThatIsRead_IsMarked_AndTheSecurityLogCountsWhatReadsIt()
    {
        ServeWhatReads(SourceId);

        await SendAsync(new SetDataSourceMigrationWritesCommand(SourceId, true, Force: true));

        _source.AllowMigrationWrites.Should().BeTrue();
        _world.Saved.OfType<AppSettingHistory>().Should().ContainSingle().Which.NewValue.Should().Be("true");
        _logs.Contains($"Data source {SourceId} is marked as a migration write target while 2 queries, 1 subscriptions and 1 data contracts read it").Should().BeTrue();
    }

    [Test]
    public async Task ASourceNothingReads_IsMarkedWithoutConfirmation_AndUnmarkingNeverAsks()
    {
        ServeWhatReads(SourceId + 1);

        await SendAsync(new SetDataSourceMigrationWritesCommand(SourceId, true));
        ServeWhatReads(SourceId);
        await SendAsync(new SetDataSourceMigrationWritesCommand(SourceId, false));

        _source.AllowMigrationWrites.Should().BeFalse();
        _world.Saved.OfType<AppSettingHistory>().Select(x => x.NewValue).Should().Equal("true", "false");
    }

    [TestCase(1, 0, 0, "is read by 1 query.")]
    [TestCase(0, 3, 0, "is read by 3 subscriptions.")]
    [TestCase(2, 0, 1, "is read by 2 queries and 1 data contract.")]
    public void TheRefusal_NamesOnlyWhatReadsTheSource(int queries, int subscriptions, int contracts, string expected)
    {
        MigrationWriteTargets.InUseMessage(SourceId, queries, subscriptions, contracts)
            .Should().StartWith($"Data source {SourceId} {expected}");
    }

    // Two queries with a step on the source (one subscribed to) and one with none; a contract on it and one elsewhere.
    private void ServeWhatReads(int dataSourceId)
    {
        var reading = QueryOn(1, dataSourceId);
        var alsoReading = QueryOn(2, SourceId + 5, dataSourceId);
        var other = QueryOn(3, SourceId + 5);
        _world.Serve(new List<Query> { reading, alsoReading, other });
        _world.Serve(new List<Subscription>
        {
            new() { Id = 1, QueryId = reading.Id, Query = reading, CronExpression = "0 * * * *" },
            new() { Id = 2, QueryId = other.Id, Query = other, CronExpression = "0 * * * *" }
        });
        _world.Serve(new List<DataContract>
        {
            new() { Id = 1, Name = "fresh orders", DataSourceId = dataSourceId, SchemaName = "sales", TableName = "orders", CronExpression = "0 * * * *" },
            new() { Id = 2, Name = "fresh invoices", DataSourceId = SourceId + 5, SchemaName = "sales", TableName = "invoices", CronExpression = "0 * * * *" }
        });
    }

    private static Query QueryOn(int id, params int[] stepSources)
    {
        var query = new Query { Id = id, Name = $"query {id}" };
        foreach (var (dataSourceId, index) in stepSources.Select((x, i) => (x, i)))
        {
            query.Steps.Add(new QueryStep { Id = id * 10 + index, QueryId = id, Query = query, StepOrder = index + 1, DataSourceId = dataSourceId, SqlValue = "SELECT 1" });
        }

        return query;
    }

    private async Task SendAsync(SetDataSourceMigrationWritesCommand command, bool enabled = true)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ext-root")], "Cookies"));
        var services = new ServiceCollection();
        _world.Register(services);
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } });
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var handler = new SetDataSourceMigrationWritesHandler(
            _world.Factory(),
            Options.Create(new MigrationOptions { Enabled = enabled }),
            scope.ServiceProvider.GetRequiredService<IBeaconActorAccessor>(),
            _cache,
            _graphs.Object,
            TimeProvider.System,
            _audit,
            _logs.For<SetDataSourceMigrationWritesHandler>());

        await handler.Handle(command, CancellationToken.None);
    }
}
