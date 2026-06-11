using System.Data.Common;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services.Providers;

namespace Beacon.Connector.PostgreSql;

public class PostgreSqlMetadataExtractor : IDatabaseMetadataExtractor
{
    public DatabaseEngineType SupportedEngineType => DatabaseEngineType.PostgreSQL;

    public async Task<IReadOnlyList<TableMetadataDto>> ExtractMetadataAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string tablesQuery = @"
            SELECT
                n.nspname AS table_schema,
                c.relname AS table_name,
                a.attname AS column_name,
                pg_catalog.format_type(a.atttypid, a.atttypmod) AS data_type,
                CASE WHEN a.attnotnull THEN 'NO' ELSE 'YES' END AS is_nullable,
                pg_get_expr(ad.adbin, ad.adrelid) AS column_default,
                CASE
                    WHEN t.typname IN ('varchar', 'char', 'bpchar') THEN
                        CASE WHEN a.atttypmod > 0 THEN a.atttypmod - 4 ELSE NULL END
                    ELSE NULL
                END AS character_maximum_length,
                a.attnum AS ordinal_position,
                EXISTS(
                    SELECT 1
                    FROM pg_constraint con
                    WHERE con.conrelid = c.oid
                    AND con.contype = 'p'
                    AND a.attnum = ANY(con.conkey)
                ) AS is_primary_key
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid
            JOIN pg_type t ON a.atttypid = t.oid
            LEFT JOIN pg_attrdef ad ON a.attrelid = ad.adrelid AND a.attnum = ad.adnum
            WHERE c.relkind IN ('r', 'p')
                AND n.nspname NOT IN ('pg_catalog', 'information_schema')
                AND a.attnum > 0
                AND NOT a.attisdropped";

        var columnsData = await connection.QueryAsync(new CommandDefinition(tablesQuery, commandTimeout: 180, cancellationToken: cancellationToken));

        const string foreignKeysQuery = @"
            SELECT
                n.nspname AS table_schema,
                c.relname AS table_name,
                a.attname AS column_name,
                fn.nspname AS foreign_schema_name,
                fc.relname AS foreign_table_name,
                fa.attname AS foreign_column_name
            FROM pg_constraint con
            JOIN pg_class c ON con.conrelid = c.oid
            JOIN pg_namespace n ON c.relnamespace = n.oid
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = ANY(con.conkey)
            JOIN pg_class fc ON con.confrelid = fc.oid
            JOIN pg_namespace fn ON fc.relnamespace = fn.oid
            JOIN pg_attribute fa ON fa.attrelid = fc.oid AND fa.attnum = ANY(con.confkey)
            WHERE con.contype = 'f'
                AND n.nspname NOT IN ('pg_catalog', 'information_schema')
                AND array_position(con.conkey, a.attnum) = array_position(con.confkey, fa.attnum)";

        var foreignKeys = await connection.QueryAsync(new CommandDefinition(foreignKeysQuery, commandTimeout: 180, cancellationToken: cancellationToken));
        var fkLookup = foreignKeys
            .GroupBy(fk => $"{fk.table_schema}.{fk.table_name}.{fk.column_name}")
            .ToDictionary(
                g => g.Key,
                g => (TableName: (string)g.First().foreign_table_name, ColumnName: (string)g.First().foreign_column_name)
            );

        const string indexesQuery = @"
            SELECT
                schemaname AS table_schema,
                tablename AS table_name,
                indexname AS index_name,
                indexdef
            FROM pg_indexes
            WHERE schemaname NOT IN ('pg_catalog', 'information_schema')";

        var indexesData = await connection.QueryAsync(new CommandDefinition(indexesQuery, commandTimeout: 180, cancellationToken: cancellationToken));

        var tables = columnsData
            .GroupBy(c => new { schema = (string)c.table_schema, table = (string)c.table_name })
            .Select(g =>
            {
                var columns = g.Select(c =>
                {
                    var fkKey = $"{g.Key.schema}.{g.Key.table}.{c.column_name}";
                    var hasFk = fkLookup.TryGetValue(fkKey, out var fkInfo);

                    return new ColumnMetadataDto(
                        ColumnName: (string)c.column_name,
                        DataType: (string)c.data_type,
                        IsNullable: ((string)c.is_nullable).Equals("YES", StringComparison.OrdinalIgnoreCase),
                        IsPrimaryKey: (bool)c.is_primary_key,
                        IsForeignKey: hasFk,
                        OrdinalPosition: (int)c.ordinal_position,
                        ForeignKeyTable: hasFk ? fkInfo.TableName : null,
                        ForeignKeyColumn: hasFk ? fkInfo.ColumnName : null,
                        DefaultValue: c.column_default?.ToString(),
                        MaxLength: c.character_maximum_length as int?,
                        Description: null
                    );
                }).ToList();

                var indexes = indexesData
                    .Where(i => (string)i.table_schema == g.Key.schema && (string)i.table_name == g.Key.table)
                    .Select(i =>
                    {
                        var indexDef = (string)i.indexdef;
                        var isPrimaryKey = indexDef.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase);
                        var isUnique = indexDef.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) || isPrimaryKey;

                        // Match the column list greedily after USING so expression indexes with
                        // nested parentheses (e.g. lower(name)) keep their full definition.
                        var usingIndex = indexDef.IndexOf(" USING ", StringComparison.OrdinalIgnoreCase);
                        var columnsSource = usingIndex >= 0 ? indexDef[usingIndex..] : indexDef;
                        var columnsMatch = Regex.Match(columnsSource, @"\((.*)\)");
                        var columnNames = columnsMatch.Success
                            ? columnsMatch.Groups[1].Value
                                .Split(',')
                                .Select(c => Regex.Replace(c.Trim(), @"\s+(DESC|ASC|NULLS\s+(FIRST|LAST))\b", "", RegexOptions.IgnoreCase).Trim())
                                .ToArray()
                            : Array.Empty<string>();

                        return new IndexMetadataDto(
                            IndexName: (string)i.index_name,
                            IsUnique: isUnique,
                            IsPrimaryKey: isPrimaryKey,
                            Columns: columnNames
                        );
                    }).ToList();

                return new TableMetadataDto(
                    SchemaName: g.Key.schema,
                    TableName: g.Key.table,
                    Columns: columns,
                    Indexes: indexes,
                    Description: null
                );
            })
            .ToList();

        return tables;
    }
}
