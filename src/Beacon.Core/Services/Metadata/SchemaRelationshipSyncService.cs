using Beacon.Core.Data;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Services.Metadata;

internal sealed class SchemaRelationshipSyncService(IDbContextFactory<BeaconContext> contextFactory)
    : ISchemaRelationshipSyncService
{
    public async Task<SchemaRelationshipSyncResult> SyncAsync(int dataSourceId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var tables = await LoadTablesAsync(context, dataSourceId, cancellationToken);

        // Archived rows must be loaded too. The unique edge-identity index covers them, so re-inserting
        // an edge a user deleted would violate the constraint; and honouring the archive is the point —
        // sync must not resurrect a relationship somebody deliberately removed.
        var existing = await context.SchemaRelationships
            .IgnoreQueryFilters()
            .Where(x => x.DataSourceId == dataSourceId)
            .ToListAsync(cancellationToken);

        var existingByKey = existing.ToDictionary(EdgeKey, StringComparer.OrdinalIgnoreCase);

        var foreignKeyEdges = DeriveForeignKeyRelationships(tables);
        var foreignKeyKeys = foreignKeyEdges
            .Select(EdgeKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = new List<SchemaRelationship>();
        foreach (var edge in foreignKeyEdges)
        {
            if (existingByKey.ContainsKey(EdgeKey(edge)))
            {
                continue;
            }

            added.Add(ToEntity(dataSourceId, edge));
        }

        // A foreign key that no longer exists in the source is archived, but only if this row came from a
        // foreign key — a user's manual or verified-inferred edge is theirs to remove, not sync's.
        var archived = 0;
        var staleForeignKeys = existing
            .Where(x => x.ArchivedTime == null)
            .Where(x => x.Origin == SchemaRelationshipOrigin.ForeignKey)
            .Where(x => !foreignKeyKeys.Contains(EdgeKey(x)))
            .ToList();

        foreach (var stale in staleForeignKeys)
        {
            stale.Archive();
            archived++;
        }

        // Inference must not re-propose anything already covered — by a foreign key, by a previously
        // persisted inference, or by a manual edge (including one the user archived on purpose).
        var covered = existingByKey.Keys
            .Concat(foreignKeyKeys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var inferred = InferRelationships(tables, covered);
        foreach (var edge in inferred)
        {
            added.Add(ToEntity(dataSourceId, edge));
        }

        context.SchemaRelationships.AddRange(added);
        await context.SaveChangesAsync(cancellationToken);

        return new SchemaRelationshipSyncResult(
            ForeignKeyAdded: added.Count(x => x.Origin == SchemaRelationshipOrigin.ForeignKey),
            ForeignKeyArchived: archived,
            InferredAdded: added.Count(x => x.Origin == SchemaRelationshipOrigin.Inferred));
    }

    public async Task<IReadOnlyList<ProposedSchemaRelationship>> PreviewAsync(int dataSourceId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var tables = await LoadTablesAsync(context, dataSourceId, cancellationToken);
        var existingKeys = (await context.SchemaRelationships
                .IgnoreQueryFilters()
                .Where(x => x.DataSourceId == dataSourceId)
                .Select(x =>
                    new
                    {
                        x.SourceSchema,
                        x.SourceTable,
                        x.SourceColumn,
                        x.TargetSchema,
                        x.TargetTable,
                        x.TargetColumn
                    })
                .ToListAsync(cancellationToken))
            .Select(x => EdgeKey(x.SourceSchema, x.SourceTable, x.SourceColumn, x.TargetSchema, x.TargetTable, x.TargetColumn))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var proposals = new List<ProposedSchemaRelationship>();
        var foreignKeyEdges = DeriveForeignKeyRelationships(tables);

        foreach (var edge in foreignKeyEdges)
        {
            if (!existingKeys.Contains(EdgeKey(edge)))
            {
                proposals.Add(edge);
            }
        }

        var covered = existingKeys
            .Concat(foreignKeyEdges.Select(EdgeKey))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        proposals.AddRange(InferRelationships(tables, covered));

        return proposals;
    }

    /// <summary>
    /// Turns declared foreign keys into edges. The target is resolved schema-qualified; an unresolvable
    /// target (dropped table, ambiguous unqualified name) yields no edge rather than a dangling one.
    /// </summary>
    internal static IReadOnlyList<ProposedSchemaRelationship> DeriveForeignKeyRelationships(
        IReadOnlyList<TableMetadataDto> tables)
    {
        var edges = new List<ProposedSchemaRelationship>();

        foreach (var table in tables)
        {
            var foreignKeyColumns = table.Columns
                .Where(x => !string.IsNullOrEmpty(x.ForeignKeyTable))
                .Where(x => !string.IsNullOrEmpty(x.ForeignKeyColumn))
                .ToList();

            foreach (var column in foreignKeyColumns)
            {
                var target = ForeignKeyTargetResolver.Resolve(tables, table.SchemaName, column);
                if (target == null)
                {
                    continue;
                }

                edges.Add(new ProposedSchemaRelationship(
                    table.SchemaName,
                    table.TableName,
                    column.ColumnName,
                    target.SchemaName,
                    target.TableName,
                    column.ForeignKeyColumn!,
                    DeriveLabel(column.ColumnName),
                    SchemaRelationshipOrigin.ForeignKey,
                    DetermineCardinality(table, column.ColumnName),
                    column.ForeignKeyConstraintName,
                    Confidence: 1.0));
            }
        }

        return edges;
    }

    /// <summary>
    /// Proposes edges from column naming for sources that declare no foreign keys. Rules are scored and
    /// tried highest-first; within the winning tier a same-schema candidate beats cross-schema ones, and
    /// anything still ambiguous is dropped — a wrong join is worse than a missing one.
    /// </summary>
    internal static IReadOnlyList<ProposedSchemaRelationship> InferRelationships(
        IReadOnlyList<TableMetadataDto> tables,
        IReadOnlySet<string> coveredEdgeKeys)
    {
        var primaryKeys = tables
            .Select(x =>
                new
                {
                    Table = x,
                    Keys = x.Columns.Where(y => y.IsPrimaryKey).ToList()
                })
            .Where(x => x.Keys.Count == 1)
            .ToDictionary(x => x.Table, x => x.Keys[0]);

        var edges = new List<ProposedSchemaRelationship>();

        foreach (var table in tables)
        {
            foreach (var column in table.Columns)
            {
                // A declared foreign key is ground truth; inference only fills gaps.
                if (!string.IsNullOrEmpty(column.ForeignKeyTable))
                {
                    continue;
                }

                var candidates = RankCandidates(table, column, primaryKeys);
                if (candidates.Count == 0)
                {
                    continue;
                }

                var bestScore = candidates.Max(x => x.Score);
                var topTier = candidates
                    .Where(x => Math.Abs(x.Score - bestScore) < 0.0001)
                    .ToList();

                var sameSchema = topTier
                    .Where(x => x.Target.SchemaName.Equals(table.SchemaName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var surviving = sameSchema.Count > 0 ? sameSchema : topTier;
                if (surviving.Count != 1)
                {
                    continue;
                }

                var winner = surviving[0];
                var targetKey = primaryKeys[winner.Target];
                var key = EdgeKey(
                    table.SchemaName, table.TableName, column.ColumnName,
                    winner.Target.SchemaName, winner.Target.TableName, targetKey.ColumnName);

                if (coveredEdgeKeys.Contains(key))
                {
                    continue;
                }

                edges.Add(new ProposedSchemaRelationship(
                    table.SchemaName,
                    table.TableName,
                    column.ColumnName,
                    winner.Target.SchemaName,
                    winner.Target.TableName,
                    targetKey.ColumnName,
                    DeriveLabel(column.ColumnName),
                    SchemaRelationshipOrigin.Inferred,
                    DetermineCardinality(table, column.ColumnName),
                    ConstraintName: null,
                    Confidence: winner.Score));
            }
        }

        return edges;
    }

    /// <summary>
    /// pgGraph's <c>edge_label()</c>: the source column with an `_id` / `_fk` suffix stripped, so
    /// `customer_id` reads as `customer`.
    /// </summary>
    internal static string DeriveLabel(string columnName)
    {
        var label = columnName;

        if (label.EndsWith("_id", StringComparison.OrdinalIgnoreCase))
        {
            label = label[..^3];
        }
        else if (label.EndsWith("_fk", StringComparison.OrdinalIgnoreCase))
        {
            label = label[..^3];
        }
        else if (label.Length > 2 && label.EndsWith("Id", StringComparison.Ordinal))
        {
            label = label[..^2];
        }

        return label.Length == 0 ? columnName : label;
    }

    private static List<(TableMetadataDto Target, double Score)> RankCandidates(
        TableMetadataDto sourceTable,
        ColumnMetadataDto column,
        IReadOnlyDictionary<TableMetadataDto, ColumnMetadataDto> primaryKeys)
    {
        var candidates = new List<(TableMetadataDto Target, double Score)>();
        var stem = StripKeySuffix(column.ColumnName);

        foreach (var entry in primaryKeys)
        {
            var target = entry.Key;
            var targetKey = entry.Value;

            var isSelfReference = target.SchemaName.Equals(sourceTable.SchemaName, StringComparison.OrdinalIgnoreCase)
                && target.TableName.Equals(sourceTable.TableName, StringComparison.OrdinalIgnoreCase);
            if (isSelfReference)
            {
                continue;
            }

            // Rule 1 — `<entity>_id` / `<entity>Id` against a table named for that entity.
            if (stem != null && MatchesEntityName(stem, target.TableName))
            {
                candidates.Add((target, 0.9));
                continue;
            }

            // Rule 2 — `<table>_<pk>`, e.g. `customer_code` against customer.code.
            var qualifiedName = $"{target.TableName}_{targetKey.ColumnName}";
            if (column.ColumnName.Equals(qualifiedName, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add((target, 0.75));
                continue;
            }

            // Rule 3 — the column shares its name with another table's primary key. Generic names such
            // as `id` match every table, so they never survive the single-candidate rule below.
            var sharesPrimaryKeyName = column.ColumnName.Length > 2
                && !column.IsPrimaryKey
                && column.ColumnName.Equals(targetKey.ColumnName, StringComparison.OrdinalIgnoreCase);
            if (sharesPrimaryKeyName)
            {
                candidates.Add((target, 0.6));
            }
        }

        return candidates;
    }

    private static bool MatchesEntityName(string stem, string tableName)
    {
        if (tableName.Equals(stem, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return tableName.Equals($"{stem}s", StringComparison.OrdinalIgnoreCase)
            || tableName.Equals($"{stem}es", StringComparison.OrdinalIgnoreCase);
    }

    private static string? StripKeySuffix(string columnName)
    {
        if (columnName.EndsWith("_id", StringComparison.OrdinalIgnoreCase) && columnName.Length > 3)
        {
            return columnName[..^3];
        }

        if (columnName.Length > 2 && columnName.EndsWith("Id", StringComparison.Ordinal) && char.IsLower(columnName[^3]))
        {
            return columnName[..^2];
        }

        return null;
    }

    private static SchemaRelationshipCardinality DetermineCardinality(TableMetadataDto table, string columnName)
    {
        var isSolePrimaryKey = table.Columns.Count(x => x.IsPrimaryKey) == 1
            && table.Columns.Any(x => x.IsPrimaryKey && x.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase));
        if (isSolePrimaryKey)
        {
            return SchemaRelationshipCardinality.OneToOne;
        }

        var hasSingleColumnUniqueIndex = table.Indexes
            .Where(x => x.IsUnique)
            .Where(x => x.Columns.Length == 1)
            .Any(x => x.Columns[0].Equals(columnName, StringComparison.OrdinalIgnoreCase));

        return hasSingleColumnUniqueIndex
            ? SchemaRelationshipCardinality.OneToOne
            : SchemaRelationshipCardinality.OneToMany;
    }

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
                    Columns = x.Columns
                        .OrderBy(y => y.OrdinalPosition)
                        .Select(y =>
                            new
                            {
                                y.ColumnName,
                                y.DataType,
                                y.IsNullable,
                                y.IsPrimaryKey,
                                y.IsForeignKey,
                                y.OrdinalPosition,
                                y.ForeignKeyTable,
                                y.ForeignKeyColumn,
                                y.ForeignKeySchema,
                                y.ForeignKeyConstraintName
                            })
                        .ToList(),
                    Indexes = x.Indexes
                        .Select(y =>
                            new
                            {
                                y.IndexName,
                                y.IsUnique,
                                y.IsPrimaryKey,
                                y.Columns
                            })
                        .ToList()
                })
            .ToListAsync(cancellationToken);

        return rows
            .Select(x =>
                new TableMetadataDto(
                    x.SchemaName,
                    x.TableName,
                    x.Columns
                        .Select(y =>
                            new ColumnMetadataDto(
                                y.ColumnName,
                                y.DataType,
                                y.IsNullable,
                                y.IsPrimaryKey,
                                y.IsForeignKey,
                                y.OrdinalPosition,
                                y.ForeignKeyTable,
                                y.ForeignKeyColumn,
                                null,
                                null,
                                null,
                                null,
                                y.ForeignKeySchema,
                                y.ForeignKeyConstraintName))
                        .ToList(),
                    x.Indexes
                        .Select(y =>
                            new IndexMetadataDto(
                                y.IndexName,
                                y.IsUnique,
                                y.IsPrimaryKey,
                                y.Columns))
                        .ToList(),
                    null))
            .ToList();
    }

    private static SchemaRelationship ToEntity(int dataSourceId, ProposedSchemaRelationship edge) =>
        new()
        {
            DataSourceId = dataSourceId,
            SourceSchema = edge.SourceSchema,
            SourceTable = edge.SourceTable,
            SourceColumn = edge.SourceColumn,
            TargetSchema = edge.TargetSchema,
            TargetTable = edge.TargetTable,
            TargetColumn = edge.TargetColumn,
            Label = edge.Label,
            Origin = edge.Origin,
            Cardinality = edge.Cardinality,
            ConstraintName = edge.ConstraintName,
            Confidence = edge.Confidence,
            IsVerified = edge.Origin != SchemaRelationshipOrigin.Inferred,
            VerifiedByUserId = null,
            VerifiedTime = null
        };

    private static string EdgeKey(ProposedSchemaRelationship edge) =>
        EdgeKey(edge.SourceSchema, edge.SourceTable, edge.SourceColumn, edge.TargetSchema, edge.TargetTable, edge.TargetColumn);

    private static string EdgeKey(SchemaRelationship relationship) =>
        EdgeKey(relationship.SourceSchema, relationship.SourceTable, relationship.SourceColumn,
            relationship.TargetSchema, relationship.TargetTable, relationship.TargetColumn);

    private static string EdgeKey(
        string sourceSchema, string sourceTable, string sourceColumn,
        string targetSchema, string targetTable, string targetColumn) =>
        $"{sourceSchema}|{sourceTable}|{sourceColumn}|{targetSchema}|{targetTable}|{targetColumn}";
}
