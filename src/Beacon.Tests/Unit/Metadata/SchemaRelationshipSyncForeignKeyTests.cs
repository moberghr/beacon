using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services.Metadata;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Metadata;

/// <summary>
/// Relationship sync from stored metadata where one column takes part in several foreign keys: a single-column key
/// <c>orders.tenant_id -> tenants.id</c> and a composite key <c>orders(tenant_id, x_id) -> x(tenant_id, id)</c>.
/// </summary>
[TestFixture]
public class SchemaRelationshipSyncForeignKeyTests
{
    private const int DataSourceId = 5;

    private List<DatabaseMetadata> _metadata = null!;
    private List<SchemaRelationship> _relationships = null!;
    private SchemaRelationshipSyncService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _metadata =
        [
            Table("tenants", Column("id", 1, pk: true)),
            Table("x", Column("tenant_id", 1, pk: true), Column("id", 2, pk: true)),
            Table(
                "orders",
                Column("tenant_id", 1, foreignKeys:
                [
                    new ColumnForeignKeyReference("fk_orders_tenants", "app", "tenants", "id"),
                    new ColumnForeignKeyReference("fk_orders_x", "app", "x", "tenant_id")
                ]),
                Column("x_id", 2, foreignKeys: [new ColumnForeignKeyReference("fk_orders_x", "app", "x", "id")]))
        ];
        _relationships = [];

        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object>
                {
                    [typeof(DatabaseMetadata)] = RecordingBeaconContext.MemorySet(_metadata, []),
                    [typeof(SchemaRelationship)] = RelationshipSet()
                },
                []));

        _service = new SchemaRelationshipSyncService(factory.Object);
    }

    [Test]
    public async Task SyncCreatesOneRelationshipPerForeignKeyEdgeIncludingBothPairsOfTheCompositeKey()
    {
        var result = await _service.SyncAsync(DataSourceId);

        // x.tenant_id -> tenants.id is also proposed, by naming (x.tenant_id declares no foreign key); only the declared edges are pinned.
        result.ForeignKeyAdded.Should().Be(3);
        result.ForeignKeyArchived.Should().Be(0);
        _relationships
            .Where(x => x.Origin == SchemaRelationshipOrigin.ForeignKey)
            .Select(x => (x.SourceTable, x.SourceColumn, x.TargetSchema, x.TargetTable, x.TargetColumn, x.ConstraintName, x.Origin))
            .Should().BeEquivalentTo(new[]
            {
                ("orders", "tenant_id", "app", "tenants", "id", "fk_orders_tenants", SchemaRelationshipOrigin.ForeignKey),
                ("orders", "tenant_id", "app", "x", "tenant_id", "fk_orders_x", SchemaRelationshipOrigin.ForeignKey),
                ("orders", "x_id", "app", "x", "id", "fk_orders_x", SchemaRelationshipOrigin.ForeignKey)
            });
        _relationships
            .Where(x => x.Origin == SchemaRelationshipOrigin.ForeignKey)
            .Should().OnlyContain(x => x.DataSourceId == DataSourceId && x.IsVerified && x.ArchivedTime == null);
    }

    [Test]
    public async Task ResyncOfUnchangedMetadataAddsAndArchivesNothing()
    {
        await _service.SyncAsync(DataSourceId);
        var firstRun = _relationships.ToList();

        var result = await _service.SyncAsync(DataSourceId);

        result.Should().Be(new SchemaRelationshipSyncResult(ForeignKeyAdded: 0, ForeignKeyArchived: 0, InferredAdded: 0));
        _relationships.Should().Equal(firstRun);
        _relationships.Should().OnlyContain(x => x.ArchivedTime == null);
    }

    [Test]
    public async Task PreviewAfterSyncProposesNothingAndBeforeSyncProposesEveryEdge()
    {
        var before = await _service.PreviewAsync(DataSourceId);
        await _service.SyncAsync(DataSourceId);

        var after = await _service.PreviewAsync(DataSourceId);

        before
            .Where(x => x.Origin == SchemaRelationshipOrigin.ForeignKey)
            .Select(x => (x.SourceColumn, x.TargetTable, x.TargetColumn))
            .Should().BeEquivalentTo(new[]
        {
            ("tenant_id", "tenants", "id"),
            ("tenant_id", "x", "tenant_id"),
            ("x_id", "x", "id")
        });
        after.Should().BeEmpty();
    }

    [Test]
    public async Task ColumnStoredWithoutTheListStillYieldsItsSingleForeignKeyEdge()
    {
        _metadata.Clear();
        _metadata.Add(Table("tenants", Column("id", 1, pk: true)));
        _metadata.Add(Table("users", Column("id", 1, pk: true), Column("tenant_id", 2, foreignKeys: [new ColumnForeignKeyReference("fk_users_tenants", "app", "tenants", "id")])));

        var result = await _service.SyncAsync(DataSourceId);

        result.ForeignKeyAdded.Should().Be(1);
        _relationships.Single().Should().Match<SchemaRelationship>(x =>
            x.SourceColumn == "tenant_id" && x.TargetTable == "tenants" && x.TargetColumn == "id" && x.ConstraintName == "fk_users_tenants");
    }

    [Test]
    public async Task HostMergeThatDropsAColumnsForeignKeysLeavesNoEdgeForThem()
    {
        await _service.SyncAsync(DataSourceId);
        var dataSource = new DataSource { Id = DataSourceId, Name = "host", DataSourceType = DataSourceType.Database, EncryptedConnectionData = "encrypted" };
        var exposed = new List<TableMetadataDto>
        {
            new("app", "tenants", [Dto("id", 1, pk: true)], [], null),
            new("app", "x", [Dto("tenant_id", 1, pk: true), Dto("id", 2, pk: true)], [], null),
            new("app", "orders", [Dto("tenant_id", 1), Dto("x_id", 2) with { IsForeignKey = true, ForeignKeyTable = "x", ForeignKeyColumn = "id", ForeignKeySchema = "app", ForeignKeyConstraintName = "fk_orders_x" }], [], null)
        };

        HostMetadataMerger.Merge(dataSource, _metadata, exposed, DateTime.UtcNow);
        var result = await _service.SyncAsync(DataSourceId);

        _metadata.Single(x => x.TableName == "orders").Columns.Should().OnlyContain(x => x.ForeignKeys == null);
        result.ForeignKeyAdded.Should().Be(0);
        result.ForeignKeyArchived.Should().Be(2);
        _relationships
            .Where(x => x.Origin == SchemaRelationshipOrigin.ForeignKey)
            .Where(x => x.ArchivedTime == null)
            .Select(x => (x.SourceColumn, x.TargetTable, x.TargetColumn))
            .Should().Equal(("x_id", "x", "id"));
    }

    [Test]
    public void StoredFormKeepsTheListOnlyForAColumnInSeveralForeignKeysAndReadsItBack()
    {
        var several = new List<ColumnForeignKeyReference>
        {
            new("fk_a", "app", "tenants", "id"),
            new("fk_b", "app", "x", "tenant_id")
        };

        ColumnForeignKeys.Serialize([several[0]]).Should().BeNull();
        ColumnForeignKeys.Serialize(null).Should().BeNull();
        ColumnForeignKeys.Deserialize(ColumnForeignKeys.Serialize(several)).Should().Equal(several);
        ColumnForeignKeys.Deserialize("not json").Should().BeNull();
        ColumnForeignKeys.Deserialize(null).Should().BeNull();
    }

    private DbSet<SchemaRelationship> RelationshipSet()
    {
        var set = RecordingBeaconContext.MemorySet(_relationships, []);
        Mock.Get(set)
            .Setup(x => x.AddRange(It.IsAny<IEnumerable<SchemaRelationship>>()))
            .Callback<IEnumerable<SchemaRelationship>>(x => _relationships.AddRange(x));
        return set;
    }

    private static DatabaseMetadata Table(string name, params ColumnMetadata[] columns)
    {
        var table = new DatabaseMetadata
        {
            DataSourceId = DataSourceId,
            SchemaName = "app",
            TableName = name
        };

        foreach (var column in columns)
        {
            table.Columns.Add(column);
        }

        return table;
    }

    private static ColumnMetadataDto Dto(string name, int ordinal, bool pk = false)
    {
        return new ColumnMetadataDto(name, "integer", false, pk, false, ordinal, null, null, null, null, null);
    }

    // The column as relationship sync reads it back from storage: the single-target columns hold the first foreign key in
    // constraint-name order, the list column the rest only when there are several (ColumnForeignKeys.Serialize).
    private static ColumnMetadata Column(string name, int ordinal, bool pk = false, IReadOnlyList<ColumnForeignKeyReference>? foreignKeys = null)
    {
        var first = foreignKeys?[0];
        return new ColumnMetadata
        {
            ColumnName = name,
            DataType = "integer",
            IsPrimaryKey = pk,
            IsForeignKey = first != null,
            OrdinalPosition = ordinal,
            ForeignKeyTable = first?.TargetTable,
            ForeignKeyColumn = first?.TargetColumn,
            ForeignKeySchema = first?.TargetSchema,
            ForeignKeyConstraintName = first?.ConstraintName,
            ForeignKeys = ColumnForeignKeys.Serialize(foreignKeys)
        };
    }
}
