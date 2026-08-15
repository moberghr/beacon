using Beacon.Core.Data.Enums;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Beacon.Tests.Integration;

/// <summary>
/// Query-translation coverage (§4.3) for the schema-relationship reads. Each mirrors a real query and is
/// validated via <c>ToQueryString()</c> against the Npgsql provider on a dummy connection (no DB hit —
/// §4.7), so a provider-side translation break fails here rather than at runtime.
/// </summary>
[TestFixture]
public class SchemaRelationshipTranslationTests : QueryTranslationTestBase
{
    [Test]
    public void GetSchemaRelationshipsByDataSource_Translates()
    {
        // Mirrors GetSchemaRelationshipsHandler and the graph build in SchemaGraphService.
        var sql = Context.SchemaRelationships
            .Where(x => x.DataSourceId == 1)
            .OrderBy(x => x.SourceSchema)
            .ThenBy(x => x.SourceTable)
            .Select(x =>
                new
                {
                    x.Id,
                    x.SourceSchema,
                    x.SourceTable,
                    x.SourceColumn,
                    x.TargetSchema,
                    x.TargetTable,
                    x.TargetColumn,
                    x.Label,
                    x.Origin,
                    x.Cardinality,
                    x.Confidence,
                    x.IsVerified
                })
            .ToQueryString();

        sql.Should().NotBeNullOrEmpty();
        sql.ToLowerInvariant().Should().Contain("schema_relationships");
    }

    [Test]
    public void GetSchemaRelationshipsFilteredByOrigin_Translates()
    {
        // Mirrors the unverified-proposal count in GetSchemaHealthHandler.
        var sql = Context.SchemaRelationships
            .Where(x => x.DataSourceId == 1)
            .Where(x => x.Origin == SchemaRelationshipOrigin.Inferred)
            .Where(x => !x.IsVerified)
            .Select(x => x.Id)
            .ToQueryString();

        sql.Should().NotBeNullOrEmpty();
        sql.ToLowerInvariant().Should().Contain("schema_relationships");
        sql.ToLowerInvariant().Should().Contain("origin");
    }

    [Test]
    public void SchemaRelationshipQuery_AppliesSoftDeleteFilter()
    {
        // SchemaRelationship is an ArchivableBaseEntity, so the global query filter must reach the SQL —
        // an archived relationship must never enter the graph.
        var sql = Context.SchemaRelationships
            .Where(x => x.DataSourceId == 1)
            .Select(x => x.Id)
            .ToQueryString();

        sql.ToLowerInvariant().Should().Contain("archived_time");
    }

    [Test]
    public void SchemaRelationshipSyncLoad_IgnoresQueryFiltersSoArchivedEdgesStayVisible()
    {
        // Regression: the unique edge-identity index covers archived rows, so sync must see them.
        // Loading with the soft-delete filter applied hides a deleted edge, sync re-inserts it, and the
        // unique constraint blows up — swallowed by the fail-closed wrapper, so sync silently dies
        // forever after the first delete.
        var sql = Context.SchemaRelationships
            .IgnoreQueryFilters()
            .Where(x => x.DataSourceId == 1)
            .Select(x => x.Id)
            .ToQueryString();

        sql.ToLowerInvariant().Should().NotContain("archived_time",
            "sync loads archived relationships deliberately, so no soft-delete predicate may be applied");
    }

    [Test]
    public void SchemaRelationshipEdgeIdentityLookup_Translates()
    {
        // Mirrors the duplicate check in CreateSchemaRelationshipHandler.
        var sql = Context.SchemaRelationships
            .Where(x => x.DataSourceId == 1)
            .Where(x => x.SourceSchema == "sales")
            .Where(x => x.SourceTable == "orders")
            .Where(x => x.SourceColumn == "customer_id")
            .Where(x => x.TargetSchema == "sales")
            .Where(x => x.TargetTable == "customers")
            .Where(x => x.TargetColumn == "id")
            .Select(x => x.Id)
            .ToQueryString();

        sql.Should().NotBeNullOrEmpty();
        sql.ToLowerInvariant().Should().Contain("source_column");
        sql.ToLowerInvariant().Should().Contain("target_column");
    }
}
