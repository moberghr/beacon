using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;
using Beacon.Core.Models.Metadata;

namespace Beacon.Core.Services.Validation;

/// <summary>
/// Deterministic semantic checks over SQL that already parses, applied on top of the schema-existence
/// check <see cref="SqlSchemaValidator"/> already performs. Catches classes of *runnable but wrong* SQL
/// (spec item ⑥): a join on a pair that is not a declared relationship, an aggregate fanning out over a
/// one-to-many join before the aggregate runs, and a non-aggregated SELECT column missing from GROUP BY.
/// Pure and stateless — no I/O, no schema round-trip. A parse failure returns an empty finding list; the
/// read-only validator already owns parse-failure rejection, so this linter never re-reports it.
/// </summary>
public sealed class SqlSemanticLinter
{
    private static readonly HashSet<string> AggregateFunctionNames =
        new(StringComparer.OrdinalIgnoreCase) { "SUM", "AVG", "COUNT" };

    /// <param name="sql">The SQL to lint.</param>
    /// <param name="dialect">Database engine name (PostgreSQL, SqlServer, MySQL, BigQuery, …).</param>
    /// <param name="context">Known join edges, primary keys and the schema catalog, all keyed the same
    /// way <see cref="SqlSchemaValidator"/> keys its catalog (lowercase <c>table</c> and
    /// lowercase <c>schema.table</c>).</param>
    public IReadOnlyList<SqlLintFinding> Lint(string sql, string? dialect, SchemaLintContext context)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return [];
        }

        Sequence<Statement> statements;
        try
        {
            statements = new Parser().ParseSql(sql, ResolveDialect(dialect));
        }
        catch (Exception)
        {
            // Fail-closed toward silence, not toward blocking: the AST read-only validator already
            // owns parse-failure rejection (§1.5), so an unparseable statement here just means "no
            // semantic opinion", not "reject".
            return [];
        }

        var findings = new List<SqlLintFinding>();
        foreach (var statement in statements)
        {
            if (statement is Statement.Select select)
            {
                LintQuery(select.Query, context, findings);
            }
        }

        return findings;
    }

    private static void LintQuery(Query query, SchemaLintContext context, List<SqlLintFinding> findings, IReadOnlySet<string>? ambientOpaque = null)
    {
        var opaque = ambientOpaque != null
            ? new HashSet<string>(ambientOpaque, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (query.With != null)
        {
            foreach (var cte in query.With.CteTables)
            {
                opaque.Add(cte.Alias.Name.Value);
            }

            foreach (var cte in query.With.CteTables)
            {
                LintQuery(cte.Query, context, findings, opaque);
            }
        }

        ProcessBody(query.Body, context, findings, opaque);
    }

    private static void ProcessBody(SetExpression body, SchemaLintContext context, List<SqlLintFinding> findings, IReadOnlySet<string> ambientOpaque)
    {
        switch (body)
        {
            case SetExpression.SelectExpression selectExpression:
                LintSelect(selectExpression.Select, context, findings, ambientOpaque);
                return;

            case SetExpression.SetOperation setOperation:
                ProcessBody(setOperation.Left, context, findings, ambientOpaque);
                ProcessBody(setOperation.Right, context, findings, ambientOpaque);
                return;

            case SetExpression.QueryExpression queryExpression:
                LintQuery(queryExpression.Query, context, findings, ambientOpaque);
                return;
        }
    }

    private static void LintSelect(Select select, SchemaLintContext context, List<SqlLintFinding> findings, IReadOnlySet<string> ambientOpaque)
    {
        var scope = new QueryScope();
        foreach (var name in ambientOpaque)
        {
            scope.Opaque.Add(name);
        }

        var equalities = new List<JoinEquality>();

        if (select.From != null)
        {
            foreach (var tableWithJoins in select.From)
            {
                ProcessTableWithJoins(tableWithJoins, scope, context, equalities, findings, ambientOpaque);
            }
        }

        if (select.Selection != null)
        {
            CollectEqualities(select.Selection, scope, equalities, topLevelAnd: true);
        }

        CheckUndeclaredJoins(equalities, context, findings);
        CheckFanoutAggregate(select, scope, context, equalities, findings);
        CheckGroupByMismatch(select, findings);
    }

    private static void ProcessTableWithJoins(TableWithJoins tableWithJoins, QueryScope scope, SchemaLintContext context, List<JoinEquality> equalities, List<SqlLintFinding> findings, IReadOnlySet<string> ambientOpaque)
    {
        if (tableWithJoins.Relation != null)
        {
            ProcessTableFactor(tableWithJoins.Relation, scope, context, equalities, findings, ambientOpaque);
        }

        if (tableWithJoins.Joins == null)
        {
            return;
        }

        foreach (var join in tableWithJoins.Joins)
        {
            if (join.Relation != null)
            {
                ProcessTableFactor(join.Relation, scope, context, equalities, findings, ambientOpaque);
            }

            ExtractOnEqualities(join.JoinOperator, scope, equalities);
        }
    }

    private static void ProcessTableFactor(TableFactor factor, QueryScope scope, SchemaLintContext context, List<JoinEquality> equalities, List<SqlLintFinding> findings, IReadOnlySet<string> ambientOpaque)
    {
        switch (factor)
        {
            case TableFactor.Table table:
                RegisterTable(table, scope, context.Catalog);
                return;

            case TableFactor.Derived derived:
                if (derived.Alias != null)
                {
                    scope.Opaque.Add(derived.Alias.Name.Value);
                }

                // A derived table's own FROM/JOIN clauses have no visibility into this scope's
                // aliases (standard SQL scoping) — lint it as an independent nested query.
                LintQuery(derived.SubQuery, context, findings, ambientOpaque);
                return;

            case TableFactor.NestedJoin { TableWithJoins: { } nested }:
                // Parenthesized join list — same FROM scope, so process in place.
                ProcessTableWithJoins(nested, scope, context, equalities, findings, ambientOpaque);
                return;

            default:
                // Table-valued functions, PIVOT/UNPIVOT, UNNEST, etc. — their column sets are not in
                // the catalog, so treat the alias (if any) as opaque to avoid false positives.
                if (factor.Alias != null)
                {
                    scope.Opaque.Add(factor.Alias.Name.Value);
                }

                return;
        }
    }

    private static void RegisterTable(TableFactor.Table table, QueryScope scope, IReadOnlyDictionary<string, HashSet<string>> catalog)
    {
        var parts = table.Name.Values.Select(x => x.Value).ToList();
        var tableName = parts[^1];
        var alias = table.Alias?.Name.Value;

        // A reference to a CTE (or another opaque relation) by name, not a real table.
        if (parts.Count == 1 && scope.Opaque.Contains(tableName))
        {
            if (alias != null)
            {
                scope.Opaque.Add(alias);
            }

            return;
        }

        var qualifiedName = string.Join(".", parts);
        var catalogKey = ResolveCatalogKey(qualifiedName, tableName, catalog);

        scope.Aliases[tableName] = catalogKey;
        if (alias != null)
        {
            scope.Aliases[alias] = catalogKey;
        }

        if (parts.Count > 1)
        {
            scope.Aliases[qualifiedName] = catalogKey;
        }
    }

    private static void ExtractOnEqualities(JoinOperator? joinOperator, QueryScope scope, List<JoinEquality> equalities)
    {
        if (joinOperator is JoinOperator.ConstrainedJoinOperator { JoinConstraint: JoinConstraint.On on })
        {
            CollectEqualities(on.Expression, scope, equalities, topLevelAnd: true);
        }
    }

    // Walks a top-level AND-chain (ON or WHERE) looking for equalities between two column references
    // qualified to two DIFFERENT, non-opaque table aliases. Does not descend into OR branches or
    // sub-expressions other than AND/parentheses — a simple, conservative net that keeps false
    // positives low, matching the rest of this validator family's "skip when ambiguous" posture.
    private static void CollectEqualities(Expression expression, QueryScope scope, List<JoinEquality> equalities, bool topLevelAnd)
    {
        switch (expression)
        {
            case Expression.BinaryOp { Op: BinaryOperator.And } andOp when topLevelAnd:
                CollectEqualities(andOp.Left, scope, equalities, topLevelAnd: true);
                CollectEqualities(andOp.Right, scope, equalities, topLevelAnd: true);
                return;

            case Expression.BinaryOp { Op: BinaryOperator.Eq } eq:
                var left = TryResolveColumnRef(eq.Left, scope);
                var right = TryResolveColumnRef(eq.Right, scope);
                if (left != null && right != null && !string.Equals(left.Value.Table, right.Value.Table, StringComparison.OrdinalIgnoreCase))
                {
                    equalities.Add(new JoinEquality(left.Value.Table, left.Value.Column, right.Value.Table, right.Value.Column));
                }

                return;

            case Expression.Nested nested:
                CollectEqualities(nested.Expression, scope, equalities, topLevelAnd);
                return;
        }
    }

    private static (string Table, string Column)? TryResolveColumnRef(Expression expression, QueryScope scope)
    {
        if (expression is not Expression.CompoundIdentifier compound || compound.Idents.Count < 2)
        {
            return null;
        }

        var idents = compound.Idents.Select(x => x.Value).ToList();
        var qualifier = string.Join(".", idents.Take(idents.Count - 1));
        var column = idents[^1];

        if (scope.Opaque.Contains(qualifier))
        {
            return null;
        }

        return scope.Aliases.TryGetValue(qualifier, out var tableKey) ? (tableKey, column) : null;
    }

    private static void CheckUndeclaredJoins(List<JoinEquality> equalities, SchemaLintContext context, List<SqlLintFinding> findings)
    {
        foreach (var equality in equalities.Distinct())
        {
            var declared = context.KnownJoins.Any(step =>
                (StepMatches(step.FromQualifiedName, step.FromColumn, equality.TableA, equality.ColA)
                    && StepMatches(step.ToQualifiedName, step.ToColumn, equality.TableB, equality.ColB))
                || (StepMatches(step.FromQualifiedName, step.FromColumn, equality.TableB, equality.ColB)
                    && StepMatches(step.ToQualifiedName, step.ToColumn, equality.TableA, equality.ColA)));

            if (declared)
            {
                continue;
            }

            var knownPaths = context.KnownJoins
                .Where(step =>
                    (TableMatches(step.FromQualifiedName, equality.TableA) && TableMatches(step.ToQualifiedName, equality.TableB))
                    || (TableMatches(step.FromQualifiedName, equality.TableB) && TableMatches(step.ToQualifiedName, equality.TableA)))
                .ToList();

            var message = knownPaths.Count > 0
                ? $"Join between '{equality.TableA}' and '{equality.TableB}' on ({equality.ColA} = {equality.ColB}) does not match a declared relationship. Known path(s): "
                    + string.Join(", ", knownPaths.Select(x => $"{x.FromQualifiedName}.{x.FromColumn} = {x.ToQualifiedName}.{x.ToColumn}")) + "."
                : $"Join between '{equality.TableA}' and '{equality.TableB}' on ({equality.ColA} = {equality.ColB}) does not match any declared relationship.";

            findings.Add(new SqlLintFinding("UNDECLARED_JOIN", message));
        }
    }

    private static void CheckFanoutAggregate(Select select, QueryScope scope, SchemaLintContext context, List<JoinEquality> equalities, List<SqlLintFinding> findings)
    {
        if (equalities.Count == 0)
        {
            return;
        }

        var reported = new HashSet<(string AggTable, string OtherTable, string OtherColumn)>();

        foreach (var item in select.Projection)
        {
            var expression = GetSelectItemExpression(item);
            if (expression == null)
            {
                continue;
            }

            foreach (var aggregate in FindAggregateFunctions(expression))
            {
                var argColumn = GetSingleColumnArg(aggregate);
                if (argColumn == null)
                {
                    continue;
                }

                var aggTable = ResolveColumnTable(argColumn.Value.Qualifier, scope);
                if (aggTable == null)
                {
                    continue;
                }

                foreach (var equality in equalities)
                {
                    string? otherTable;
                    string? otherColumn;
                    if (string.Equals(equality.TableA, aggTable, StringComparison.OrdinalIgnoreCase))
                    {
                        otherTable = equality.TableB;
                        otherColumn = equality.ColB;
                    }
                    else if (string.Equals(equality.TableB, aggTable, StringComparison.OrdinalIgnoreCase))
                    {
                        otherTable = equality.TableA;
                        otherColumn = equality.ColA;
                    }
                    else
                    {
                        continue;
                    }

                    // Primary key unknown → skip. Flagging with no PK evidence would be a guess, not
                    // a finding (same "skip when ambiguous" posture as SqlSchemaValidator).
                    if (!context.PrimaryKeys.TryGetValue(otherTable, out var primaryKey) || primaryKey.Count == 0)
                    {
                        continue;
                    }

                    var isFullPrimaryKey = primaryKey.Count == 1 && primaryKey.Contains(otherColumn.ToLowerInvariant());
                    if (isFullPrimaryKey)
                    {
                        continue;
                    }

                    if (reported.Add((aggTable, otherTable, otherColumn)))
                    {
                        findings.Add(new SqlLintFinding(
                            "FANOUT_AGGREGATE",
                            $"Aggregate over '{aggTable}' is joined to '{otherTable}' on '{otherColumn}', which is not the full primary key of '{otherTable}'. '{otherTable}' can contribute more than one row per '{aggTable}' row, inflating the aggregate."));
                    }
                }
            }
        }
    }

    private static void CheckGroupByMismatch(Select select, List<SqlLintFinding> findings)
    {
        var hasAggregate = select.Projection.Any(item =>
            GetSelectItemExpression(item) is { } expression && FindAggregateFunctions(expression).Any());

        if (!hasAggregate)
        {
            return;
        }

        // GROUP BY ALL groups by every non-aggregated projection automatically — no mismatch is possible.
        if (select.GroupBy is GroupByExpression.All)
        {
            return;
        }

        var groupByKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groupedProjectionIndexes = new HashSet<int>();
        if (select.GroupBy is GroupByExpression.Expressions groupByExpressions)
        {
            foreach (var groupByItem in groupByExpressions.ColumnNames)
            {
                switch (groupByItem)
                {
                    case Expression.Identifier identifier:
                        groupByKeys.Add(identifier.Ident.Value);
                        break;

                    case Expression.CompoundIdentifier compound:
                        var idents = compound.Idents.Select(x => x.Value).ToList();
                        groupByKeys.Add(idents[^1]);
                        groupByKeys.Add(string.Join(".", idents));
                        break;

                    case Expression.LiteralValue { Value: Value.Number number }
                        when int.TryParse(number.Value, out var position) && position >= 1 && position <= select.Projection.Count:
                        // Positional GROUP BY (`GROUP BY 1, 2`, PostgreSQL/MySQL/etc.) names the
                        // projection item at that 1-based index.
                        groupedProjectionIndexes.Add(position - 1);
                        break;
                }
            }
        }

        for (var index = 0; index < select.Projection.Count; index++)
        {
            var item = select.Projection[index];
            var expression = GetSelectItemExpression(item);
            if (expression == null || FindAggregateFunctions(expression).Any())
            {
                continue;
            }

            if (groupedProjectionIndexes.Contains(index))
            {
                continue;
            }

            string column;
            string? qualified = null;
            switch (expression)
            {
                case Expression.Identifier identifier:
                    column = identifier.Ident.Value;
                    break;

                case Expression.CompoundIdentifier compound:
                    var idents = compound.Idents.Select(x => x.Value).ToList();
                    column = idents[^1];
                    qualified = string.Join(".", idents);
                    break;

                default:
                    // A constant, expression or CASE — not a plain column reference. Skip rather than
                    // guess (§3.x "non-constant" is a narrowing rule, not licence to over-report).
                    continue;
            }

            // NF2: GROUP BY can reference the projection's OWN alias (e.g. `o.customer_id AS cust ...
            // GROUP BY cust`) rather than the underlying column — accept that as a match too.
            var alias = GetSelectItemAlias(item);
            var presentInGroupBy = groupByKeys.Contains(column)
                || (qualified != null && groupByKeys.Contains(qualified))
                || (alias != null && groupByKeys.Contains(alias));
            if (!presentInGroupBy)
            {
                findings.Add(new SqlLintFinding(
                    "GROUP_BY_MISMATCH",
                    $"Column '{column}' is selected without an aggregate but is not in GROUP BY."));
            }
        }
    }

    private static Expression? GetSelectItemExpression(SelectItem item)
    {
        return item switch
        {
            SelectItem.UnnamedExpression unnamed => unnamed.Expression,
            SelectItem.ExpressionWithAlias aliased => aliased.Expression,
            _ => null
        };
    }

    private static string? GetSelectItemAlias(SelectItem item)
    {
        return item is SelectItem.ExpressionWithAlias aliased
            ? aliased.Alias.Value
            : null;
    }

    private static IEnumerable<Expression.Function> FindAggregateFunctions(Expression expression)
    {
        switch (expression)
        {
            case Expression.Function function:
                // COUNT(*) OVER (...) is a window function: it never collapses rows, so it neither
                // requires a GROUP BY nor fans out over a join. Only a bare aggregate call counts.
                if (function.Over == null && IsAggregateName(function.Name))
                {
                    yield return function;
                }

                yield break;

            case Expression.BinaryOp binaryOp:
                foreach (var found in FindAggregateFunctions(binaryOp.Left))
                {
                    yield return found;
                }

                foreach (var found in FindAggregateFunctions(binaryOp.Right))
                {
                    yield return found;
                }

                yield break;

            case Expression.UnaryOp unaryOp:
                foreach (var found in FindAggregateFunctions(unaryOp.Expression))
                {
                    yield return found;
                }

                yield break;

            case Expression.Nested nested:
                foreach (var found in FindAggregateFunctions(nested.Expression))
                {
                    yield return found;
                }

                yield break;

            case Expression.Cast cast:
                foreach (var found in FindAggregateFunctions(cast.Expression))
                {
                    yield return found;
                }

                yield break;
        }
    }

    private static bool IsAggregateName(ObjectName name)
    {
        var last = name.Values.LastOrDefault()?.Value;
        return last != null && AggregateFunctionNames.Contains(last);
    }

    private static (string? Qualifier, string Column)? GetSingleColumnArg(Expression.Function function)
    {
        if (function.Args is not FunctionArguments.List list || list.ArgumentList.Args is not { Count: 1 } args)
        {
            return null;
        }

        if (args[0] is not FunctionArg.Unnamed { FunctionArgExpression: FunctionArgExpression.FunctionExpression functionExpression })
        {
            return null;
        }

        return functionExpression.Expression switch
        {
            Expression.CompoundIdentifier compound when compound.Idents.Count >= 2 =>
                (string.Join(".", compound.Idents.Take(compound.Idents.Count - 1).Select(x => x.Value)), compound.Idents[^1].Value),
            Expression.Identifier identifier => (null, identifier.Ident.Value),
            _ => null
        };
    }

    private static string? ResolveColumnTable(string? qualifier, QueryScope scope)
    {
        if (qualifier != null)
        {
            if (scope.Opaque.Contains(qualifier))
            {
                return null;
            }

            return scope.Aliases.TryGetValue(qualifier, out var tableKey) ? tableKey : null;
        }

        // Bare column — only safe to attribute when exactly one real table is registered in this scope.
        var distinctKeys = scope.Aliases.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return distinctKeys.Count == 1 ? distinctKeys[0] : null;
    }

    // Prefers a schema-qualified catalog key (matching SchemaJoinStep.FromQualifiedName /
    // ToQualifiedName, which are always "schema.table") so a bare table reference in the SQL still
    // matches known joins recorded against the qualified name. Falls back to the bare name, mirroring
    // SqlSchemaValidator.FindCatalogKey.
    private static string ResolveCatalogKey(string qualifiedName, string tableName, IReadOnlyDictionary<string, HashSet<string>> catalog)
    {
        var qualifiedLower = qualifiedName.ToLowerInvariant();
        if (qualifiedLower.Contains('.') && catalog.ContainsKey(qualifiedLower))
        {
            return qualifiedLower;
        }

        var tableLower = tableName.ToLowerInvariant();
        var qualifiedMatch = catalog.Keys.FirstOrDefault(x => x.Contains('.') && x.EndsWith($".{tableLower}", StringComparison.Ordinal));
        if (qualifiedMatch != null)
        {
            return qualifiedMatch;
        }

        return catalog.ContainsKey(tableLower) ? tableLower : qualifiedLower;
    }

    private static bool StepMatches(string stepQualifiedName, string stepColumn, string table, string column)
    {
        return TableMatches(stepQualifiedName, table) && string.Equals(stepColumn, column, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TableMatches(string stepQualifiedName, string table)
    {
        return string.Equals(stepQualifiedName, table, StringComparison.OrdinalIgnoreCase)
            || stepQualifiedName.EndsWith($".{table}", StringComparison.OrdinalIgnoreCase)
            || table.EndsWith($".{stepQualifiedName}", StringComparison.OrdinalIgnoreCase);
    }

    private static Dialect ResolveDialect(string? dialect)
    {
        return (dialect ?? "").ToLowerInvariant() switch
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
    }

    private sealed class QueryScope
    {
        public Dictionary<string, string> Aliases { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Opaque { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly record struct JoinEquality(string TableA, string ColA, string TableB, string ColB);
}

/// <summary>One deterministic semantic finding. <see cref="Code"/> is one of the three rule codes.</summary>
public sealed record SqlLintFinding(string Code, string Message);

/// <summary>
/// Everything <see cref="SqlSemanticLinter"/> needs, keyed exactly like <see cref="SqlSchemaValidator"/>'s
/// catalog (lowercase <c>table</c> and lowercase <c>schema.table</c>).
/// </summary>
/// <param name="KnownJoins">Every hop of every join path the schema graph knows about.</param>
/// <param name="PrimaryKeys">Primary-key column set per table (lowercase column names).</param>
/// <param name="Catalog">Column set per table, for resolving bare table references to a qualified key.</param>
public sealed record SchemaLintContext(
    IReadOnlyList<SchemaJoinStep> KnownJoins,
    IReadOnlyDictionary<string, IReadOnlySet<string>> PrimaryKeys,
    IReadOnlyDictionary<string, HashSet<string>> Catalog);
