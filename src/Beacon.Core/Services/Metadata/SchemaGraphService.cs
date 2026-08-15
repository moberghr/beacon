using Beacon.Core.Data;
using Beacon.Core.Models.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Beacon.Core.Services.Metadata;

internal sealed class SchemaGraphService(
    IDbContextFactory<BeaconContext> contextFactory,
    IMemoryCache cache)
    : ISchemaGraphService
{
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(15);

    public async Task<SchemaGraph> GetGraphAsync(int dataSourceId, CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey(dataSourceId), out SchemaGraph? cached) && cached != null)
        {
            return cached;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var tables = await LoadTablesAsync(context, dataSourceId, cancellationToken);
        var relationships = await context.SchemaRelationships
            .AsNoTracking()
            .Where(x => x.DataSourceId == dataSourceId)
            .Select(x =>
                new SchemaRelationshipEdge(
                    x.SourceSchema,
                    x.SourceTable,
                    x.SourceColumn,
                    x.TargetSchema,
                    x.TargetTable,
                    x.TargetColumn,
                    x.Label,
                    x.Origin,
                    x.IsVerified,
                    x.Confidence))
            .ToListAsync(cancellationToken);

        var graph = SchemaGraph.Build(tables, relationships);
        cache.Set(CacheKey(dataSourceId), graph, CacheExpiration);

        return graph;
    }

    public async Task<SchemaHealthReport> GetHealthAsync(int dataSourceId, CancellationToken cancellationToken = default)
    {
        var graph = await GetGraphAsync(dataSourceId, cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var verified = await context.SchemaRelationships
            .Where(x => x.DataSourceId == dataSourceId)
            .Where(x => x.IsVerified)
            .CountAsync(cancellationToken);

        var unverified = await context.SchemaRelationships
            .Where(x => x.DataSourceId == dataSourceId)
            .Where(x => !x.IsVerified)
            .CountAsync(cancellationToken);

        var components = graph.ConnectedComponents();

        return new SchemaHealthReport(
            TableCount: graph.Nodes.Count,
            RelationshipCount: graph.EdgeCount,
            VerifiedRelationshipCount: verified,
            UnverifiedRelationshipCount: unverified,
            ComponentCount: components.Count,
            LargestComponentSize: components.Count == 0 ? 0 : components.Max(x => x.Count),
            IsolatedTables: graph.IsolatedTables(),
            JunctionTables: graph.JunctionTables());
    }

    public void Invalidate(int dataSourceId) => cache.Remove(CacheKey(dataSourceId));

    private static string CacheKey(int dataSourceId) => $"schema-graph:{dataSourceId}";

    private static async Task<List<TableMetadataDto>> LoadTablesAsync(
        BeaconContext context,
        int dataSourceId,
        CancellationToken cancellationToken)
    {
        var rows = await context.DatabaseMetadata
            .AsNoTracking()
            .Where(x => x.DataSourceId == dataSourceId)
            .Select(x =>
                new
                {
                    x.SchemaName,
                    x.TableName,
                    PrimaryKeyColumns = x.Columns
                        .Where(y => y.IsPrimaryKey)
                        .Select(y => y.ColumnName)
                        .ToList()
                })
            .ToListAsync(cancellationToken);

        return rows
            .Select(x =>
                new TableMetadataDto(
                    x.SchemaName,
                    x.TableName,
                    x.PrimaryKeyColumns
                        .Select(y => new ColumnMetadataDto(y, string.Empty, false, true, false, 0, null, null, null, null, null))
                        .ToList(),
                    [],
                    null))
            .ToList();
    }
}
