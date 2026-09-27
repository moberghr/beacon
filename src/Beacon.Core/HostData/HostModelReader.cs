using System.Security.Cryptography;
using System.Text;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Beacon.Core.HostData;

/// <summary>
/// Turns a host EF model into the exposed metadata + execution policy. Pure — reads the in-memory model only,
/// never opens a connection. Default deny: a table is exposed only when allow-listed; a column is dropped when a
/// host predicate or the secret-like hard-exclude says so.
/// </summary>
internal static class HostModelReader
{
    public const string SqlServerProviderName = "Microsoft.EntityFrameworkCore.SqlServer";
    public const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>Appended to a masked column's description so agents see the PII flag in every catalog view.</summary>
    public const string MaskedDescriptionMarker = "[PII: values are masked in query results]";

    public static DatabaseEngineType InferEngine(string? providerName, DatabaseEngineType? configured)
    {
        var inferred = providerName switch
        {
            SqlServerProviderName => DatabaseEngineType.MSSQL,
            NpgsqlProviderName => DatabaseEngineType.PostgreSQL,
            _ => (DatabaseEngineType?)null
        };

        var engine = configured ?? inferred;
        if (engine is not (DatabaseEngineType.MSSQL or DatabaseEngineType.PostgreSQL))
        {
            throw new InvalidOperationException(
                $"ExposeDbContext supports SQL Server and PostgreSQL contexts only (EF provider '{providerName ?? "unknown"}').");
        }

        if (configured.HasValue && inferred.HasValue && configured != inferred)
        {
            throw new InvalidOperationException(
                $"ExposeDbContext Engine is set to {configured} but the context uses the {inferred} provider.");
        }

        return engine.Value;
    }

    public static HostExposureSnapshot Read(
        IModel model,
        DatabaseEngineType engine,
        HostDataSourceRegistration registration,
        IXmlDocumentationProvider documentation)
    {
        var options = registration.Options;
        var defaultSchema = model.GetDefaultSchema() ?? (engine == DatabaseEngineType.MSSQL ? "dbo" : "public");

        var storeObjects = GroupByStoreObject(model, defaultSchema);
        var allowed = storeObjects
            .Where(x => IsAllowed(x, options))
            .ToList();

        ThrowOnUnknownAllowListEntries(storeObjects, options);

        var allowedKeys = allowed
            .Select(x => StoreKey(x.Schema, x.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var partitions = allowed.ToDictionary(
            x => StoreKey(x.Schema, x.Name),
            x => PartitionColumns(x, options),
            StringComparer.OrdinalIgnoreCase);

        var tables = new List<TableMetadataDto>();
        var policyTables = new List<HostExposedTable>();

        var orderedAllowed = allowed
            .OrderBy(x => x.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var storeObject in orderedAllowed)
        {
            var partition = partitions[StoreKey(storeObject.Schema, storeObject.Name)];
            var foreignKeys = ResolveForeignKeys(storeObject, storeObjects, allowedKeys, partitions, defaultSchema);

            var columns = partition.Columns
                .Where(x => !x.Excluded)
                .Select((x, index) => ToColumnDto(x, index, foreignKeys, documentation))
                .ToList();

            tables.Add(new TableMetadataDto(
                storeObject.Schema,
                storeObject.Name,
                columns,
                ResolveIndexes(storeObject, partition),
                DescribeTable(storeObject, documentation)));

            policyTables.Add(new HostExposedTable(
                storeObject.Schema,
                storeObject.Name,
                partition.Columns
                    .Where(x => !x.Excluded)
                    .Select(x => x.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                partition.Columns
                    .Where(x => x.Excluded)
                    .Select(x => x.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                partition.Columns
                    .Where(x => x.Masked)
                    .Select(x => x.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)));
        }

        var policy = new HostExposurePolicy(engine, defaultSchema, policyTables, options.AllowedFunctions);

        return new HostExposureSnapshot(engine, tables, policy, ComputeHash(registration, engine, tables, policyTables));
    }

    private static List<StoreObjectGroup> GroupByStoreObject(IModel model, string defaultSchema)
    {
        var groups = new Dictionary<string, StoreObjectGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var entityType in model.GetEntityTypes())
        {
            var storeObject = ResolveStoreObject(entityType);
            if (storeObject == null)
            {
                // Mapped to a SQL query / function or not mapped at all — nothing addressable to expose.
                continue;
            }

            var schema = storeObject.Value.Schema ?? defaultSchema;
            var key = StoreKey(schema, storeObject.Value.Name);
            if (!groups.TryGetValue(key, out var group))
            {
                group = new StoreObjectGroup(storeObject.Value, schema, storeObject.Value.Name, storeObject.Value.StoreObjectType == StoreObjectType.View, []);
                groups[key] = group;
            }

            group.EntityTypes.Add(entityType);
        }

        return groups.Values.ToList();
    }

    private static StoreObjectIdentifier? ResolveStoreObject(IEntityType entityType)
    {
        var tableName = entityType.GetTableName();
        if (tableName != null)
        {
            return StoreObjectIdentifier.Table(tableName, entityType.GetSchema());
        }

        var viewName = entityType.GetViewName();

        return viewName != null ? StoreObjectIdentifier.View(viewName, entityType.GetViewSchema()) : null;
    }

    private static bool IsAllowed(StoreObjectGroup group, HostDbContextOptions options)
    {
        if (options.AllowAll)
        {
            return true;
        }

        if (options.AllowedTables.Any(x => MatchesTableName(x, group)))
        {
            return true;
        }

        return group.EntityTypes.Any(x => options.AllowedEntityTypes.Contains(x.ClrType));
    }

    private static bool MatchesTableName(string allowListEntry, StoreObjectGroup group)
    {
        return allowListEntry.Equals(group.Name, StringComparison.OrdinalIgnoreCase)
            || allowListEntry.Equals(StoreKey(group.Schema, group.Name), StringComparison.OrdinalIgnoreCase);
    }

    private static void ThrowOnUnknownAllowListEntries(List<StoreObjectGroup> groups, HostDbContextOptions options)
    {
        var unknownTables = options.AllowedTables
            .Where(x => !groups.Any(y => MatchesTableName(x, y)))
            .ToList();

        var unknownEntities = options.AllowedEntityTypes
            .Where(x => !groups.Any(y => y.EntityTypes.Any(z => z.ClrType == x)))
            .Select(x => x.Name)
            .ToList();

        if (unknownTables.Count == 0 && unknownEntities.Count == 0)
        {
            return;
        }

        var parts = new List<string>();
        if (unknownTables.Count > 0)
        {
            parts.Add($"tables not in the model: {string.Join(", ", unknownTables)}");
        }

        if (unknownEntities.Count > 0)
        {
            parts.Add($"entity types not mapped to a table or view: {string.Join(", ", unknownEntities)}");
        }

        throw new InvalidOperationException($"ExposeDbContext allow-list has entries that match nothing ({string.Join("; ", parts)}).");
    }

    private static ColumnPartition PartitionColumns(StoreObjectGroup group, HostDbContextOptions options)
    {
        var columns = new List<HostColumnInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ownerType = PrimaryEntityType(group);

        foreach (var entityType in group.EntityTypes)
        {
            foreach (var property in entityType.GetProperties())
            {
                var columnName = property.GetColumnName(group.StoreObject);
                if (columnName == null || !seen.Add(columnName))
                {
                    continue;
                }

                var hostColumn = new HostColumn(
                    group.Schema,
                    group.Name,
                    columnName,
                    property.ClrType,
                    (entityType.IsOwned() ? ownerType : entityType).ClrType,
                    property.Name);

                var excluded = options.ExcludePredicates.Any(x => x(hostColumn))
                    || IsHardExcluded(hostColumn, options);

                var masked = !excluded && options.MaskPredicates.Any(x => x(hostColumn));

                columns.Add(new HostColumnInfo(
                    columnName,
                    property,
                    property.GetColumnType(group.StoreObject) ?? property.ClrType.Name,
                    property.IsColumnNullable(group.StoreObject),
                    property.GetColumnOrder(group.StoreObject),
                    excluded,
                    masked));
            }
        }

        var primaryKey = group.EntityTypes
            .Select(x => x.FindPrimaryKey())
            .FirstOrDefault(x => x != null);

        var primaryKeyColumns = primaryKey?.Properties
            .Select(x => x.GetColumnName(group.StoreObject))
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        var ordered = columns
            .OrderBy(x => x.ColumnOrder ?? int.MaxValue)
            .ThenBy(x => primaryKeyColumns.Contains(x.Name) ? 0 : 1)
            .ToList();

        return new ColumnPartition(ordered, primaryKeyColumns, primaryKey);
    }

    private static bool IsHardExcluded(HostColumn column, HostDbContextOptions options)
    {
        if (!SecretLikeColumnNames.IsSecretLike(column.Name) && !SecretLikeColumnNames.IsSecretLike(column.PropertyName))
        {
            return false;
        }

        return !options.SecretLikeOverrides.Contains($"{column.Table}.{column.Name}")
            && !options.SecretLikeOverrides.Contains($"{column.Schema}.{column.Table}.{column.Name}");
    }

    private static Dictionary<string, ForeignKeyTarget> ResolveForeignKeys(
        StoreObjectGroup group,
        List<StoreObjectGroup> allGroups,
        HashSet<string> allowedKeys,
        Dictionary<string, ColumnPartition> partitions,
        string defaultSchema)
    {
        var result = new Dictionary<string, ForeignKeyTarget>(StringComparer.OrdinalIgnoreCase);
        var partition = partitions[StoreKey(group.Schema, group.Name)];

        foreach (var entityType in group.EntityTypes)
        {
            foreach (var foreignKey in entityType.GetForeignKeys())
            {
                var principalStore = ResolveStoreObject(foreignKey.PrincipalEntityType);
                if (principalStore == null)
                {
                    continue;
                }

                var principalSchema = principalStore.Value.Schema ?? defaultSchema;
                var principalKey = StoreKey(principalSchema, principalStore.Value.Name);

                // An FK to a table that is not exposed would point the agent at something it cannot query.
                if (!allowedKeys.Contains(principalKey))
                {
                    continue;
                }

                var dependentColumns = foreignKey.Properties
                    .Select(x => x.GetColumnName(group.StoreObject))
                    .ToList();

                var principalColumns = foreignKey.PrincipalKey.Properties
                    .Select(x => x.GetColumnName(principalStore.Value))
                    .ToList();

                if (dependentColumns.Any(x => x == null) || principalColumns.Any(x => x == null))
                {
                    continue;
                }

                // Table splitting / owned types sharing the row: the "FK" is the row's own key, not a join.
                if (principalKey.Equals(StoreKey(group.Schema, group.Name), StringComparison.OrdinalIgnoreCase)
                    && dependentColumns.SequenceEqual(principalColumns, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var principalPartition = partitions[principalKey];
                if (dependentColumns.Any(x => IsExcluded(partition, x!)) || principalColumns.Any(x => IsExcluded(principalPartition, x!)))
                {
                    continue;
                }

                var constraintName = foreignKey.GetConstraintName(group.StoreObject, principalStore.Value)
                    ?? foreignKey.GetConstraintName();

                for (var i = 0; i < dependentColumns.Count; i++)
                {
                    result.TryAdd(dependentColumns[i]!, new ForeignKeyTarget(
                        principalSchema,
                        principalStore.Value.Name,
                        principalColumns[i]!,
                        constraintName));
                }
            }
        }

        return result;
    }

    private static bool IsExcluded(ColumnPartition partition, string columnName)
    {
        return partition.Columns
            .Where(x => x.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Excluded)
            .FirstOrDefault(true);
    }

    private static ColumnMetadataDto ToColumnDto(
        HostColumnInfo column,
        int index,
        Dictionary<string, ForeignKeyTarget> foreignKeys,
        IXmlDocumentationProvider documentation)
    {
        foreignKeys.TryGetValue(column.Name, out var foreignKey);

        return new ColumnMetadataDto(
            column.Name,
            column.StoreType,
            column.IsNullable,
            column.IsPrimaryKey,
            foreignKey != null,
            index + 1,
            foreignKey?.Table,
            foreignKey?.Column,
            DefaultValue: null,
            column.Property.GetMaxLength(),
            DescribeColumn(column, documentation),
            SampleValues: null,
            ForeignKeySchema: foreignKey?.Schema,
            ForeignKeyConstraintName: foreignKey?.ConstraintName);
    }

    private static List<IndexMetadataDto> ResolveIndexes(StoreObjectGroup group, ColumnPartition partition)
    {
        var indexes = new List<IndexMetadataDto>();
        var exposed = partition.Columns
            .Where(x => !x.Excluded)
            .Select(x => x.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (partition.PrimaryKey != null && partition.PrimaryKeyColumns.Count > 0 && partition.PrimaryKeyColumns.All(exposed.Contains))
        {
            indexes.Add(new IndexMetadataDto(
                partition.PrimaryKey.GetName(group.StoreObject) ?? $"PK_{group.Name}",
                true,
                true,
                partition.PrimaryKey.Properties
                    .Select(x => x.GetColumnName(group.StoreObject)!)
                    .ToArray()));
        }

        foreach (var index in group.EntityTypes.SelectMany(x => x.GetDeclaredIndexes()))
        {
            var columns = index.Properties
                .Select(x => x.GetColumnName(group.StoreObject))
                .ToList();

            if (columns.Any(x => x == null || !exposed.Contains(x)))
            {
                continue;
            }

            var name = index.GetDatabaseName(group.StoreObject);
            if (name == null || indexes.Any(x => x.IndexName.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            indexes.Add(new IndexMetadataDto(name, index.IsUnique, false, columns.Select(x => x!).ToArray()));
        }

        return indexes;
    }

    private static string? DescribeTable(StoreObjectGroup group, IXmlDocumentationProvider documentation)
    {
        var owner = PrimaryEntityType(group);
        var description = owner.GetComment() ?? documentation.GetTypeSummary(owner.ClrType);

        return group.IsView && description != null ? $"(view) {description}" : description;
    }

    private static string? DescribeColumn(HostColumnInfo column, IXmlDocumentationProvider documentation)
    {
        var description = column.Property.GetComment();
        if (description == null && column.Property.PropertyInfo?.DeclaringType != null)
        {
            description = documentation.GetPropertySummary(column.Property.PropertyInfo.DeclaringType, column.Property.Name);
        }

        if (!column.Masked)
        {
            return description;
        }

        return description == null ? MaskedDescriptionMarker : $"{description} {MaskedDescriptionMarker}";
    }

    private static IEntityType PrimaryEntityType(StoreObjectGroup group)
    {
        return group.EntityTypes
            .Where(x => !x.IsOwned())
            .Where(x => x.BaseType == null)
            .FirstOrDefault() ?? group.EntityTypes[0];
    }

    private static string ComputeHash(
        HostDataSourceRegistration registration,
        DatabaseEngineType engine,
        List<TableMetadataDto> tables,
        List<HostExposedTable> policyTables)
    {
        var builder = new StringBuilder();
        builder.Append("v1|").Append(registration.Key).Append('|').Append(registration.Name).Append('|')
            .Append(registration.ProjectName).Append('|').Append(registration.Options.Description).Append('|')
            .Append(registration.Options.ReadOnlyConnectionStringName).Append('|').Append(engine).AppendLine();

        foreach (var table in tables)
        {
            builder.Append("T|").Append(table.SchemaName).Append('.').Append(table.TableName).Append('|').Append(table.Description).AppendLine();
            foreach (var column in table.Columns)
            {
                builder.Append("C|").Append(column.ColumnName).Append('|').Append(column.DataType).Append('|')
                    .Append(column.IsNullable).Append('|').Append(column.IsPrimaryKey).Append('|').Append(column.MaxLength).Append('|')
                    .Append(column.ForeignKeySchema).Append('.').Append(column.ForeignKeyTable).Append('.').Append(column.ForeignKeyColumn).Append('|')
                    .Append(column.ForeignKeyConstraintName).Append('|').Append(column.Description).AppendLine();
            }

            foreach (var index in table.Indexes)
            {
                builder.Append("I|").Append(index.IndexName).Append('|').Append(index.IsUnique).Append('|').Append(string.Join(",", index.Columns)).AppendLine();
            }
        }

        foreach (var table in policyTables)
        {
            builder.Append("X|").Append(table.Schema).Append('.').Append(table.Name).Append('|')
                .Append(string.Join(",", table.ExcludedColumns.Order(StringComparer.OrdinalIgnoreCase))).Append('|')
                .Append(string.Join(",", table.MaskedColumns.Order(StringComparer.OrdinalIgnoreCase))).AppendLine();
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static string StoreKey(string schema, string name) => $"{schema}.{name}";

    private sealed record StoreObjectGroup(
        StoreObjectIdentifier StoreObject,
        string Schema,
        string Name,
        bool IsView,
        List<IEntityType> EntityTypes);

    private sealed record ColumnPartition(
        List<HostColumnInfo> Columns,
        HashSet<string> PrimaryKeyColumns,
        IKey? PrimaryKey)
    {
        public List<HostColumnInfo> Columns { get; } = Columns
            .Select(x => x with { IsPrimaryKey = PrimaryKeyColumns.Contains(x.Name) })
            .ToList();
    }

    private sealed record HostColumnInfo(
        string Name,
        IProperty Property,
        string StoreType,
        bool IsNullable,
        int? ColumnOrder,
        bool Excluded,
        bool Masked)
    {
        public bool IsPrimaryKey { get; init; }
    }

    private sealed record ForeignKeyTarget(string Schema, string Table, string Column, string? ConstraintName);
}
