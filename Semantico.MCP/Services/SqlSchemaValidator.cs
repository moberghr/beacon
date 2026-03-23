using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;

namespace Semantico.MCP.Services;

/// <summary>
/// Validates generated SQL against the known schema catalog before executing against the database.
/// Catches column-doesn't-exist errors without a DB round-trip.
/// </summary>
internal sealed class SqlSchemaValidator
{
    /// <param name="sql">The SQL to validate.</param>
    /// <param name="catalog">Map of lowercase table/schema.table names to lowercase column sets.</param>
    /// <param name="dialect">Database engine name (PostgreSQL, SqlServer, MySQL, BigQuery, …).</param>
    public SqlValidationResult Validate(
        string sql,
        Dictionary<string, HashSet<string>> catalog,
        string? dialect = null)
    {
        if (string.IsNullOrWhiteSpace(sql) || catalog.Count == 0)
            return SqlValidationResult.Ok;

        Dialect sqlDialect = (dialect ?? "").ToLowerInvariant() switch
        {
            "postgresql" or "postgres" => new PostgreSqlDialect(),
            "sqlserver" or "mssql" or "microsoftsqlserver" => new MsSqlDialect(),
            "mysql" or "mariadb" => new MySqlDialect(),
            "bigquery" => new BigQueryDialect(),
            "snowflake" => new SnowflakeDialect(),
            "databricks" => new DatabricksDialect(),
            "duckdb" => new DuckDbDialect(),
            _ => new GenericDialect()
        };

        Sequence<Statement> statements;
        try
        {
            statements = new Parser().ParseSql(sql, sqlDialect);
        }
        catch (Exception ex)
        {
            return new SqlValidationResult(false, $"SQL syntax error: {ex.Message}");
        }

        var visitor = new SchemaValidationVisitor(catalog);
        statements.Visit(visitor);

        if (visitor.Errors.Count == 0)
            return SqlValidationResult.Ok;

        return new SqlValidationResult(false, string.Join("; ", visitor.Errors.Distinct()));
    }

    private sealed class SchemaValidationVisitor(Dictionary<string, HashSet<string>> catalog) : Visitor
    {
        // Maps alias (or bare table name) -> lowercase table name for catalog lookup
        private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Errors { get; } = [];

        public override ControlFlow PreVisitTableFactor(TableFactor relation)
        {
            if (relation is TableFactor.Table t)
            {
                var parts = t.Name.Values.ToList();
                var tableName = parts[^1].Value;
                var alias = t.Alias?.Name.Value;

                _aliases[tableName] = tableName.ToLowerInvariant();
                if (alias != null)
                    _aliases[alias] = tableName.ToLowerInvariant();
            }
            return ControlFlow.Continue;
        }

        public override ControlFlow PreVisitExpression(Expression expression)
        {
            if (expression is Expression.CompoundIdentifier ci)
            {
                var idents = ci.Idents.ToList();
                // alias.column (2 parts) or schema.table.column (3 parts)
                if (idents.Count == 2)
                {
                    ValidateColumnRef(idents[0].Value, idents[1].Value);
                }
                else if (idents.Count == 3)
                {
                    // Could be schema.table.column — check table.column
                    ValidateColumnRef($"{idents[0].Value}.{idents[1].Value}", idents[2].Value);
                }
            }
            return ControlFlow.Continue;
        }

        private void ValidateColumnRef(string qualifier, string columnName)
        {
            if (!_aliases.TryGetValue(qualifier, out var tableKey))
                return; // Unknown alias/qualifier — can't validate, skip

            if (!catalog.TryGetValue(tableKey, out var columns))
                return; // Table not in catalog — skip (avoid false positives)

            if (!columns.Contains(columnName.ToLowerInvariant()))
            {
                var available = string.Join(", ", columns.OrderBy(c => c).Take(20));
                Errors.Add($"Column '{columnName}' does not exist on '{qualifier}'. Available: {available}");
            }
        }
    }
}

public record SqlValidationResult(bool IsValid, string? Error)
{
    public static readonly SqlValidationResult Ok = new(true, null);
}
