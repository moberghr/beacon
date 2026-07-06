using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;

namespace Beacon.MCP.Services;

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
        visitor.ValidateCollectedReferences();

        if (visitor.Errors.Count == 0)
            return SqlValidationResult.Ok;

        return new SqlValidationResult(false, string.Join("; ", visitor.Errors.Distinct()));
    }

    private sealed class SchemaValidationVisitor(Dictionary<string, HashSet<string>> catalog) : Visitor
    {
        // Maps alias (or bare table name) -> catalog key for column lookup
        private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

        // CTE names and derived-table (subquery) aliases — their column sets are not in the
        // catalog, so columns attributed to them must not be validated.
        private readonly HashSet<string> _opaqueAliases = new(StringComparer.OrdinalIgnoreCase);

        // Tracks real tables (not CTEs/subqueries) for bare identifier validation
        private readonly List<string> _realTableKeys = [];

        // Column references are collected during the visit and validated afterwards, so the
        // result never depends on the order in which the AST exposes FROM clauses vs projections.
        private readonly List<(string Qualifier, string Column)> _qualifiedRefs = [];
        private readonly List<string> _bareRefs = [];

        public List<string> Errors { get; } = [];

        public override ControlFlow PreVisitQuery(Query query)
        {
            if (query.With != null)
            {
                foreach (var cte in query.With.CteTables)
                {
                    _opaqueAliases.Add(cte.Alias.Name.Value);
                }
            }

            return ControlFlow.Continue;
        }

        public override ControlFlow PreVisitTableFactor(TableFactor relation)
        {
            if (relation is TableFactor.Derived derived)
            {
                if (derived.Alias != null)
                {
                    _opaqueAliases.Add(derived.Alias.Name.Value);
                }

                return ControlFlow.Continue;
            }

            if (relation is TableFactor.Table t)
            {
                var parts = t.Name.Values.ToList();
                var tableName = parts[^1].Value;
                var alias = t.Alias?.Name.Value;

                // A reference to a CTE, not a real table — its alias is opaque too
                if (parts.Count == 1 && _opaqueAliases.Contains(tableName))
                {
                    if (alias != null)
                    {
                        _opaqueAliases.Add(alias);
                    }

                    return ControlFlow.Continue;
                }

                // Build schema-qualified key for catalog lookup
                var qualifiedName = string.Join(".", parts.Select(p => p.Value));
                var catalogKey = FindCatalogKey(qualifiedName, tableName);

                _aliases[tableName] = catalogKey;
                if (alias != null)
                {
                    _aliases[alias] = catalogKey;
                }

                // Also register the schema-qualified name as an alias
                if (parts.Count > 1)
                {
                    _aliases[qualifiedName] = catalogKey;
                }

                // Track real tables (ones that exist in catalog) for bare identifier validation
                if (catalog.ContainsKey(catalogKey))
                {
                    _realTableKeys.Add(catalogKey);
                }
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
                    _qualifiedRefs.Add((idents[0].Value, idents[1].Value));
                }
                else if (idents.Count == 3)
                {
                    _qualifiedRefs.Add(($"{idents[0].Value}.{idents[1].Value}", idents[2].Value));
                }
            }
            else if (expression is Expression.Identifier id)
            {
                _bareRefs.Add(id.Ident.Value);
            }

            return ControlFlow.Continue;
        }

        public void ValidateCollectedReferences()
        {
            foreach (var (qualifier, column) in _qualifiedRefs)
            {
                ValidateColumnRef(qualifier, column);
            }

            // With a CTE or derived table in scope, bare columns can't be attributed safely —
            // they may belong to the opaque relation, so skip bare validation entirely.
            if (_opaqueAliases.Count > 0)
            {
                return;
            }

            foreach (var column in _bareRefs)
            {
                ValidateBareColumnRef(column);
            }
        }

        private void ValidateColumnRef(string qualifier, string columnName)
        {
            if (_opaqueAliases.Contains(qualifier))
                return; // CTE / derived-table alias — columns unknown, skip

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

        private void ValidateBareColumnRef(string columnName)
        {
            // Only validate bare columns when there's exactly one real table
            // to avoid false positives with multi-table queries
            var distinctKeys = _realTableKeys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinctKeys.Count != 1) return;

            var tableKey = distinctKeys[0];
            if (!catalog.TryGetValue(tableKey, out var columns)) return;

            if (!columns.Contains(columnName.ToLowerInvariant()))
            {
                var available = string.Join(", ", columns.OrderBy(c => c).Take(20));
                Errors.Add($"Column '{columnName}' does not exist on '{tableKey}'. Available: {available}");
            }
        }

        private string FindCatalogKey(string qualifiedName, string tableName)
        {
            // Try schema-qualified first, then bare table name
            var qualLower = qualifiedName.ToLowerInvariant();
            if (catalog.ContainsKey(qualLower)) return qualLower;

            var tableLower = tableName.ToLowerInvariant();
            if (catalog.ContainsKey(tableLower)) return tableLower;

            return tableLower;
        }
    }
}

public record SqlValidationResult(bool IsValid, string? Error)
{
    public static readonly SqlValidationResult Ok = new(true, null);
}
