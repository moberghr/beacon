using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;

namespace Beacon.Core.Services.Validation;

/// <summary>
/// Validates generated SQL against the known schema catalog before executing against the database.
/// Catches column-doesn't-exist errors without a DB round-trip.
/// </summary>
public sealed class SqlSchemaValidator
{
    /// <param name="sql">The SQL to validate.</param>
    /// <param name="catalog">Map of lowercase table/schema.table names to lowercase column sets.</param>
    /// <param name="dialect">Database engine name (PostgreSQL, SqlServer, MySQL, BigQuery, …).</param>
    public SqlValidationResult Validate(
        string sql,
        Dictionary<string, HashSet<string>> catalog,
        string? dialect = null)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return SqlValidationResult.Ok;
        }

        Sequence<Statement> statements;
        try
        {
            statements = new Parser().ParseSql(sql, SqlDialects.Resolve(dialect));
        }
        catch (Exception ex)
        {
            return new SqlValidationResult(false, $"SQL syntax error: {ex.Message}");
        }

        var visitor = new SchemaValidationVisitor(catalog);
        visitor.RegisterScopes(statements);
        statements.Visit(visitor);
        visitor.ValidateCollectedReferences();

        var columnsUsed = visitor.GetReferencedColumns();
        var tablesUsed = visitor.GetReferencedTables();

        // An empty catalog means the schema is unknown, not that the SQL is correct — the walk still
        // reports which tables and columns the query touched, but the verdict is "not checked".
        if (catalog.Count == 0)
        {
            return new SqlValidationResult(true, null)
            {
                ColumnsUsed = columnsUsed,
                TablesUsed = tablesUsed,
                Checked = false
            };
        }

        if (visitor.Errors.Count == 0)
        {
            return new SqlValidationResult(true, null) { ColumnsUsed = columnsUsed, TablesUsed = tablesUsed };
        }

        return new SqlValidationResult(false, string.Join("; ", visitor.Errors.Distinct()))
        {
            ColumnsUsed = columnsUsed,
            TablesUsed = tablesUsed
        };
    }

    private sealed class SchemaValidationVisitor(Dictionary<string, HashSet<string>> catalog) : Visitor
    {
        // Maps alias (or bare table name) -> catalog key for column lookup
        private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

        // CTE names and derived-table (subquery) aliases — their column sets are not in the
        // catalog, so columns attributed to them must not be validated.
        private readonly HashSet<string> _opaqueAliases = new(StringComparer.OrdinalIgnoreCase);

        // Opaque relations the query actually READS FROM (a referenced CTE, a derived table). Bare columns
        // may belong to one of these, so their validation is skipped only when such a relation is in scope —
        // a CTE that is declared but never referenced must not switch the check off.
        private readonly HashSet<string> _opaqueReferenced = new(StringComparer.OrdinalIgnoreCase);

        // Tracks real tables (not CTEs/subqueries) for bare identifier validation
        private readonly List<string> _realTableKeys = [];

        // Column references are collected during the visit and validated afterwards, so the
        // result never depends on the order in which the AST exposes FROM clauses vs projections.
        private readonly List<(string Qualifier, string Column)> _qualifiedRefs = [];
        private readonly List<string> _bareRefs = [];

        // Real tables the query reads from, in first-seen order (CTE references excluded).
        private readonly List<string> _referencedTables = [];

        // Projection aliases (SELECT expr AS alias) are legal bare identifiers in GROUP BY / ORDER BY
        // but exist on no table, so they must not be validated against the catalog.
        private readonly HashSet<string> _projectionAliases = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Errors { get; } = [];

        // SqlParserCS 0.6.5's Visitor never invokes PreVisitQuery (verified 2026-09-08 with a trace
        // visitor), so query-level scope — CTE names and projection aliases — is registered by this
        // explicit pre-pass BEFORE the visitor walks table factors and expressions.
        public void RegisterScopes(IEnumerable<Statement> statements)
        {
            foreach (var statement in statements)
            {
                RegisterStatementScopes(statement);
            }
        }

        private void RegisterStatementScopes(Statement statement)
        {
            if (statement is Statement.Explain explain)
            {
                RegisterStatementScopes(explain.Statement);
                return;
            }

            if (statement is Statement.Select select)
            {
                RegisterQueryScopes(select.Query);
            }
        }

        private void RegisterQueryScopes(Query query)
        {
            if (query.With != null)
            {
                foreach (var cte in query.With.CteTables)
                {
                    _opaqueAliases.Add(cte.Alias.Name.Value);
                    RegisterQueryScopes(cte.Query);
                }
            }

            CollectProjectionAliases(query.Body);
        }

        public override ControlFlow PreVisitTableFactor(TableFactor relation)
        {
            if (relation is TableFactor.Derived derived)
            {
                if (derived.Alias != null)
                {
                    _opaqueAliases.Add(derived.Alias.Name.Value);
                    _opaqueReferenced.Add(derived.Alias.Name.Value);
                }
                else
                {
                    _opaqueReferenced.Add("<derived>");
                }

                // The derived table's own WITH clause / projection aliases scope its inner references, and the
                // SqlParserCS visitor does NOT descend into a derived table's subquery on its own (verified with a
                // trace visitor, 2026-09-09) — visit it explicitly so its tables and columns are seen.
                RegisterQueryScopes(derived.SubQuery);
                ((IElement)derived.SubQuery).Visit(this);

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
                    _opaqueReferenced.Add(tableName);
                    if (alias != null)
                    {
                        _opaqueAliases.Add(alias);
                        _opaqueReferenced.Add(alias);
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

                _referencedTables.Add(qualifiedName.ToLowerInvariant());

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
            // Subqueries in WHERE / IN / EXISTS / scalar position carry their own WITH clause and
            // projection aliases; register them before the visitor descends into their table factors.
            switch (expression)
            {
                case Expression.Subquery subquery:
                    RegisterQueryScopes(subquery.Query);
                    break;
                case Expression.InSubquery inSubquery:
                    RegisterQueryScopes(inSubquery.SubQuery);
                    break;
                case Expression.Exists exists:
                    RegisterQueryScopes(exists.SubQuery);
                    break;
            }

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

        // Column names the visited SQL references (lowercase, distinct), regardless of whether
        // they validated — the learning loop wants what the query touched, not what passed.
        public IReadOnlyList<string> GetReferencedColumns()
        {
            return _qualifiedRefs
                .Select(x => x.Column)
                .Concat(_bareRefs)
                .Select(x => x.ToLowerInvariant())
                .Distinct()
                .ToList();
        }

        // Tables the visited SQL reads from (lowercase, distinct, first-seen order). CTE names are
        // excluded — they are query-local, not schema objects.
        public IReadOnlyList<string> GetReferencedTables()
        {
            return _referencedTables
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public void ValidateCollectedReferences()
        {
            foreach (var (qualifier, column) in _qualifiedRefs)
            {
                ValidateColumnRef(qualifier, column);
            }

            // With a referenced CTE or derived table in scope, bare columns can't be attributed safely —
            // they may belong to the opaque relation, so skip bare validation entirely. A CTE that is
            // declared but never read from does not suppress the check.
            if (_opaqueReferenced.Count > 0)
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
            // A projection alias resolves to a computed expression, not a catalog column
            if (_projectionAliases.Contains(columnName))
            {
                return;
            }

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

        private void CollectProjectionAliases(SetExpression body)
        {
            if (body is SetExpression.SelectExpression selectExpression)
            {
                foreach (var item in selectExpression.Select.Projection)
                {
                    if (item is SelectItem.ExpressionWithAlias aliased)
                    {
                        _projectionAliases.Add(aliased.Alias.Value);
                    }
                }

                return;
            }

            if (body is SetExpression.SetOperation setOperation)
            {
                CollectProjectionAliases(setOperation.Left);
                CollectProjectionAliases(setOperation.Right);
                return;
            }

            // A parenthesised arm carries its own Query (and possibly its own WITH clause).
            if (body is SetExpression.QueryExpression queryExpression)
            {
                RegisterQueryScopes(queryExpression.Query);
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

    /// <summary>Column names the SQL references (lowercase, distinct), collected during the AST walk.</summary>
    public IReadOnlyList<string> ColumnsUsed { get; init; } = [];

    /// <summary>
    /// Table names the SQL reads from (lowercase, distinct, first-seen order, CTE names excluded).
    /// </summary>
    public IReadOnlyList<string> TablesUsed { get; init; } = [];

    /// <summary>
    /// False when the schema could not be checked (no catalog was supplied), so <see cref="IsValid"/>
    /// means "nothing contradicted the schema", not "verified against it".
    /// </summary>
    public bool Checked { get; init; } = true;
}
