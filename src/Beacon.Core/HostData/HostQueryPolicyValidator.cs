using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.RegularExpressions;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Validation;
using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;
using SqlParser.Tokens;

namespace Beacon.Core.HostData;

/// <summary>Outcome of a host-policy check. <see cref="MaskedOutputColumns"/> are result keys whose values must be masked.</summary>
public sealed record HostPolicyResult(bool Allowed, string? Error, IReadOnlyList<string> MaskedOutputColumns)
{
    /// <summary>Bind-parameter names (without their <c>@</c>) the statement uses; each must be supplied when it runs.</summary>
    public IReadOnlyList<string> Parameters { get; init; } = [];

    public static HostPolicyResult Reject(string error) => new(false, error, []);

    /// <summary>
    /// The rejection message when a parameter the statement uses has no value in <paramref name="supplied"/>, else
    /// null. An unbound <c>@p0</c> is left in the SQL by the driver, and on PostgreSQL <c>@</c> is then the
    /// absolute-value operator applied to a COLUMN named <c>p0</c> — so it must never run unbound.
    /// </summary>
    public string? FindUnboundParameter(IReadOnlyDictionary<string, object?>? supplied)
    {
        foreach (var parameter in Parameters)
        {
            var bound = supplied != null && supplied.Keys
                .Any(x => string.Equals(x.TrimStart('@', ':', '$'), parameter, StringComparison.OrdinalIgnoreCase));

            if (!bound)
            {
                return $"Parameter '@{parameter}' has no bound value, so the query was not run.";
            }
        }

        return null;
    }
}

/// <summary>
/// Enforces a <see cref="HostExposurePolicy"/> on SQL bound for a host-managed data source — the confidentiality
/// boundary between an AI agent (which may be prompt-injected) and the host application's database. Fail closed:
/// the statement must parse, in the POLICY's dialect (never the caller's), as one SELECT, and is then resolved the
/// way the database resolves it, with proper per-SELECT lexical scopes:
/// <list type="bullet">
/// <item>every relation is an allow-listed, schema-qualified table or view, a CTE in scope, or a derived table;</item>
/// <item>every column reference resolves to an exposed column of a relation in scope — unqualified names only
/// against the SELECT's own FROM, outer (correlated) references only when qualified; output aliases are visible only
/// to the same SELECT's ORDER BY (and PostgreSQL's GROUP BY) and to outer queries through the derived table's / CTE's
/// column list; a bare relation name is never a column (no whole-row references); unknown columns are rejected;</item>
/// <item>identifiers compare the way the dialect does (PostgreSQL: unquoted folded to lower case, then exact;
/// SQL Server: case-insensitive);</item>
/// <item>functions must be on the dialect's allow-list (<see cref="HostSqlFunctions"/>);</item>
/// <item>a masked column may appear only as a direct projection (which stays masked, through CTEs, derived tables
/// and UNION ALL), inside <c>COUNT(col)</c>, or in <c>col IS [NOT] NULL</c> — never in a predicate, join,
/// grouping, ordering, window, function or subquery value, so its value cannot be probed.</item>
/// </list>
/// </summary>
internal static class HostQueryPolicyValidator
{
    private const int MaxDepth = 400;

    private const string MaskedUseMessage =
        "may only be selected directly (optionally aliased), counted with COUNT(column) or tested with IS NULL / IS NOT NULL on a host data source";

    // Npgsql rewrites @name placeholders to $n only when a parameter of that name is supplied; Beacon's binders emit
    // @p0, @p1 …, so only that shape is recognised (and FindUnboundParameter makes sure it is supplied).
    private static readonly Regex PostgresParameterName = new("^p[0-9]{1,4}$", RegexOptions.CultureInvariant);

    // T-SQL date-part functions take a bare keyword (year, month, dd …) as their first argument; the parser reads it
    // as an identifier. Only that first argument, and only a real date-part keyword, is exempt from the column check.
    private static readonly HashSet<string> DatePartFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "DATEPART", "DATEADD", "DATEDIFF", "DATEDIFF_BIG", "DATENAME", "DATETRUNC", "DATE_BUCKET"
    };

    private static readonly HashSet<string> DateParts = new(StringComparer.OrdinalIgnoreCase)
    {
        "year", "yy", "yyyy", "quarter", "qq", "q", "month", "mm", "m", "dayofyear", "dy", "y", "day", "dd", "d",
        "week", "wk", "ww", "weekday", "dw", "w", "hour", "hh", "minute", "mi", "n", "second", "ss", "s",
        "millisecond", "ms", "microsecond", "mcs", "nanosecond", "ns", "tzoffset", "tz", "iso_week", "isowk", "isoww"
    };

    // Plain table hints are harmless (they change locking / plans, never what is read).
    private static readonly HashSet<string> HintFunctions = new(StringComparer.OrdinalIgnoreCase) { "INDEX", "FORCESEEK" };

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();

    public static HostPolicyResult Validate(string sql, HostExposurePolicy policy)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return HostPolicyResult.Reject("Query cannot be empty.");
        }

        var dialect = SqlDialects.Resolve(policy.Engine.ToString());
        var parameters = new List<string>();
        var parseable = sql;

        if (policy.Engine == DatabaseEngineType.PostgreSQL)
        {
            var (rewritten, error) = RewritePostgresParameters(sql, dialect, parameters);
            if (error != null)
            {
                return HostPolicyResult.Reject(error);
            }

            parseable = rewritten;
        }

        Sequence<Statement> statements;
        try
        {
            statements = new Parser().ParseSql(parseable, dialect);
        }
        catch (Exception ex)
        {
            return HostPolicyResult.Reject($"Host data source queries must parse as {policy.Engine} SQL: {ex.Message}");
        }

        if (statements.Count == 0)
        {
            return HostPolicyResult.Reject("Query cannot be empty.");
        }

        if (statements.Count > 1)
        {
            return HostPolicyResult.Reject("Only a single SELECT statement may run against a host data source.");
        }

        if (statements[0] is not Statement.Select select)
        {
            return HostPolicyResult.Reject("Only SELECT statements may run against a host data source.");
        }

        return new Analyzer(policy, parameters).Run(select.Query);
    }

    // The PostgreSQL grammar reads "@p0" as the absolute-value operator applied to a column p0, which is also what
    // the server sees when the driver does NOT substitute it. So every bare "@" must be a parameter placeholder
    // written flush against its name (@p0), and those are handed to the parser as "$p0" placeholders — same length,
    // same position, everything else byte-identical.
    private static (string Sql, string? Error) RewritePostgresParameters(string sql, Dialect dialect, List<string> parameters)
    {
        IList<Token> tokens;
        try
        {
            tokens = new Tokenizer().Tokenize(sql, dialect);
        }
        catch (Exception ex)
        {
            return (sql, $"Host data source queries must parse as PostgreSQL SQL: {ex.Message}");
        }

        var lineStarts = LineStarts(sql);
        char[]? buffer = null;

        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] is not AtSign atSign)
            {
                continue;
            }

            var next = i + 1 < tokens.Count ? tokens[i + 1] : null;
            var after = i + 2 < tokens.Count ? tokens[i + 2] : null;
            if (next is not Word { QuoteStyle: null } word || !PostgresParameterName.IsMatch(word.Value) || after is Period)
            {
                return (sql, "The @ operator is not allowed on host data sources; bind parameters are written @p0, @p1 … (use abs() for absolute values).");
            }

            var line = (int)atSign.Location.Line;
            var offset = line >= 1 && line <= lineStarts.Count ? lineStarts[line - 1] + (int)atSign.Location.Column - 1 : -1;
            if (offset < 0 || offset >= sql.Length || sql[offset] != '@')
            {
                return (sql, "Could not locate a bind parameter in the query text, so it was not run.");
            }

            buffer ??= sql.ToCharArray();
            buffer[offset] = '$';
            parameters.Add(word.Value);
        }

        return (buffer == null ? sql : new string(buffer), null);
    }

    private static List<int> LineStarts(string sql)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < sql.Length; i++)
        {
            if (sql[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    /// <summary>One column a relation or query exposes. <see cref="Name"/> is dialect-normalized; null when unnamed.</summary>
    private sealed record OutputColumn(string? Name, bool Masked);

    /// <summary>A relation in a SELECT's FROM: an allow-listed table, or a derived table / CTE with its output columns.</summary>
    private sealed class Relation
    {
        /// <summary>The name columns are qualified with: the alias, else the table / CTE name. Null for an unaliased derived table.</summary>
        public required string? Name { get; init; }

        /// <summary>Schema of an UNALIASED base table (so <c>dbo.Customer.Name</c> resolves); null otherwise.</summary>
        public string? Schema { get; init; }

        public HostExposedTable? Table { get; init; }

        public IReadOnlyList<OutputColumn> Columns { get; init; } = [];
    }

    private sealed class Scope(Scope? parent)
    {
        public Scope? Parent { get; } = parent;

        public List<Relation> Relations { get; } = [];

        public List<string> UsingColumns { get; } = [];
    }

    private readonly record struct ColumnHit(bool Found, bool Masked, string? Error)
    {
        public static ColumnHit Fail(string error) => new(false, false, error);
    }

    private sealed class Analyzer(HostExposurePolicy policy, List<string> parameters)
    {
        private readonly bool _postgres = policy.Engine == DatabaseEngineType.PostgreSQL;
        private readonly StringComparer _comparer = policy.Engine == DatabaseEngineType.PostgreSQL
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;
        private readonly List<string> _errors = [];
        private readonly HashSet<string> _parameters = new(parameters, StringComparer.OrdinalIgnoreCase);

        public HostPolicyResult Run(Query query)
        {
            var ctes = ImmutableDictionary<string, IReadOnlyList<OutputColumn>>.Empty.WithComparers(_comparer);
            var outputs = AnalyzeQuery(query, null, ctes, 0);

            var masked = outputs
                .Where(x => x.Masked)
                .ToList();

            if (masked.Any(x => x.Name == null))
            {
                Error("A masked column in a set operation must line up with a named column in the first SELECT.");
            }

            if (_errors.Count > 0)
            {
                return new HostPolicyResult(false, string.Join(" ", _errors.Distinct()), []);
            }

            var maskedNames = masked
                .Select(x => x.Name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new HostPolicyResult(true, null, maskedNames) { Parameters = _parameters.Order(StringComparer.Ordinal).ToList() };
        }

        private IReadOnlyList<OutputColumn> AnalyzeQuery(
            Query query,
            Scope? outer,
            ImmutableDictionary<string, IReadOnlyList<OutputColumn>> ctes,
            int depth)
        {
            if (!Enter(depth))
            {
                return [];
            }

            if (query.ForClause != null)
            {
                Error("FOR JSON / FOR XML / FOR BROWSE clauses are not allowed on host data sources.");
            }

            if (IsPresent(query.Locks))
            {
                Error("Locking clauses (FOR UPDATE / FOR SHARE) are not allowed on host data sources.");
            }

            if (IsPresent(query.LimitBy) || IsPresent(query.Settings) || IsPresent(query.FormatClause))
            {
                Error("LIMIT BY / SETTINGS / FORMAT clauses are not allowed on host data sources.");
            }

            if (query.With != null)
            {
                foreach (var cte in query.With.CteTables)
                {
                    var name = Normalize(cte.Alias.Name);

                    // T-SQL CTEs may reference themselves without RECURSIVE; PostgreSQL only with WITH RECURSIVE.
                    var selfVisible = (query.With.Recursive || !_postgres) && ReferencesTable(cte.Query, name, 0);
                    var columns = selfVisible
                        ? AnalyzeRecursiveCte(cte, name, outer, ctes, depth + 1)
                        : Rename(AnalyzeQuery(cte.Query, outer, ctes, depth + 1), cte.Alias);

                    ctes = ctes.SetItem(name, columns);
                }
            }

            var (outputs, selectScope) = AnalyzeSetExpression(query.Body, outer, ctes, depth + 1);

            if (query.OrderBy != null)
            {
                if (IsPresent(query.OrderBy.Interpolate))
                {
                    Error("ORDER BY … INTERPOLATE is not allowed on host data sources.");
                }

                // A set operation's ORDER BY sees only its output columns.
                var orderScope = selectScope ?? OutputScope(outputs, outer);
                foreach (var item in query.OrderBy.Expressions ?? [])
                {
                    ResolveOrderItem(item.Expression, outputs, orderScope, ctes, depth + 1);
                    if (IsPresent(item.WithFill))
                    {
                        Error("ORDER BY … WITH FILL is not allowed on host data sources.");
                    }
                }
            }

            var limitScope = new Scope(outer);
            WalkNode(query.Limit, limitScope, ctes, depth + 1);
            WalkNode(query.Offset, limitScope, ctes, depth + 1);
            WalkNode(query.Fetch, limitScope, ctes, depth + 1);

            return outputs;
        }

        // A recursive CTE's own name is in scope inside its body. Its columns (and whether they carry masked values)
        // are found by iterating to a fixpoint: start from the anchor arm, re-analyse the whole body with the
        // current assumption, and repeat while a column turns out to be masked that was assumed not to be.
        private IReadOnlyList<OutputColumn> AnalyzeRecursiveCte(
            CommonTableExpression cte,
            string name,
            Scope? outer,
            ImmutableDictionary<string, IReadOnlyList<OutputColumn>> ctes,
            int depth)
        {
            if (cte.Query.With != null)
            {
                Error("A WITH clause inside a recursive CTE is not allowed on host data sources.");
                return [];
            }

            var mark = _errors.Count;
            var assumed = Rename(AnalyzeSetExpression(AnchorArm(cte.Query.Body), outer, ctes, depth + 1).Outputs, cte.Alias);

            for (var iteration = 0; iteration < 16; iteration++)
            {
                _errors.RemoveRange(mark, _errors.Count - mark);

                var outputs = Rename(AnalyzeQuery(cte.Query, outer, ctes.SetItem(name, assumed), depth + 1), cte.Alias);
                var merged = outputs
                    .Select((x, i) => x with { Masked = x.Masked || (i < assumed.Count && assumed[i].Masked) })
                    .ToList();

                if (merged.Count == assumed.Count && merged.Select(x => x.Masked).SequenceEqual(assumed.Select(x => x.Masked)))
                {
                    return merged;
                }

                assumed = merged;
            }

            Error("The recursive CTE could not be analysed.");
            return assumed;
        }

        private (IReadOnlyList<OutputColumn> Outputs, Scope? SelectScope) AnalyzeSetExpression(
            SetExpression body,
            Scope? outer,
            ImmutableDictionary<string, IReadOnlyList<OutputColumn>> ctes,
            int depth)
        {
            if (!Enter(depth))
            {
                return ([], null);
            }

            switch (body)
            {
                case SetExpression.SelectExpression select:
                    return AnalyzeSelect(select.Select, outer, ctes, depth + 1);
                case SetExpression.QueryExpression nested:
                    return (AnalyzeQuery(nested.Query, outer, ctes, depth + 1), null);
                case SetExpression.SetOperation operation:
                    return (AnalyzeSetOperation(operation, outer, ctes, depth + 1), null);
                case SetExpression.ValuesExpression values:
                    return (AnalyzeValues(values.Values, outer, ctes, depth + 1), null);
                default:
                    Error("Only SELECT queries may run against a host data source.");
                    return ([], null);
            }
        }

        private List<OutputColumn> AnalyzeSetOperation(
            SetExpression.SetOperation operation,
            Scope? outer,
            ImmutableDictionary<string, IReadOnlyList<OutputColumn>> ctes,
            int depth)
        {
            var left = AnalyzeSetExpression(operation.Left, outer, ctes, depth).Outputs;
            var right = AnalyzeSetExpression(operation.Right, outer, ctes, depth).Outputs;

            // Set operations name their output after the FIRST arm; a masked value in any arm masks that position.
            var width = Math.Max(left.Count, right.Count);
            var merged = new List<OutputColumn>();
            for (var i = 0; i < width; i++)
            {
                var masked = (i < left.Count && left[i].Masked) || (i < right.Count && right[i].Masked);
                merged.Add(new OutputColumn(i < left.Count ? left[i].Name : null, masked));
            }

            var unionAll = operation.Op == SetOperator.Union
                && operation.SetQuantifier is SetQuantifier.All or SetQuantifier.AllByName;

            if (!unionAll && merged.Any(x => x.Masked))
            {
                Error("Masked columns may only be combined with UNION ALL — UNION, INTERSECT and EXCEPT compare their values.");
            }

            return merged;
        }

        private List<OutputColumn> AnalyzeValues(
            Values values,
            Scope? outer,
            ImmutableDictionary<string, IReadOnlyList<OutputColumn>> ctes,
            int depth)
        {
            var scope = new Scope(outer);
            foreach (var row in values.Rows)
            {
                WalkNode(row, scope, ctes, depth);
            }

            var width = values.Rows.FirstOrDefault()?.Count ?? 0;

            return Enumerable.Range(1, width)
                .Select(x => new OutputColumn(_postgres ? $"column{x}" : null, false))
                .ToList();
        }

        private (IReadOnlyList<OutputColumn> Outputs, Scope SelectScope) AnalyzeSelect(
            Select select,
            Scope? outer,
            ImmutableDictionary<string, IReadOnlyList<OutputColumn>> ctes,
            int depth)
        {
            var scope = new Scope(outer);

            if (select.Into != null)
            {
                Error("SELECT INTO is not allowed.");
            }

            if (IsPresent(select.LateralViews) || IsPresent(select.PreWhere) || IsPresent(select.ClusterBy)
                || IsPresent(select.DistributeBy) || IsPresent(select.SortBy) || IsPresent(select.QualifyBy)
                || IsPresent(select.ConnectBy) || IsPresent(select.ValueTableMode))
            {
                Error("That SELECT clause is not allowed on host data sources.");
            }

            foreach (var from in select.From ?? [])
            {
                AddTableWithJoins(from, scope, ctes, depth + 1, []);
            }

            WalkNode(select.Top, scope, ctes, depth + 1);

            var outputs = select.Projection
                .Select(x => AnalyzeSelectItem(x, scope, ctes, depth + 1))
                .ToList();

            switch (select.Distinct)
            {
                case DistinctFilter.Distinct when outputs.Any(x => x.Masked):
                    Error("SELECT DISTINCT is not allowed with a masked column — it compares the masked values.");
                    break;
                case DistinctFilter.On on:
                    foreach (var expression in on.ColumnNames)
                    {
                        ResolveOrderItem(expression, outputs, scope, ctes, depth + 1);
                    }

                    break;
            }

            WalkNode(select.Selection, scope, ctes, depth + 1);

            switch (select.GroupBy)
            {
                case GroupByExpression.Expressions groupBy:
                    foreach (var expression in groupBy.ColumnNames ?? [])
                    {
                        ResolveGroupItem(expression, outputs, scope, ctes, depth + 1);
                    }

                    break;
                case GroupByExpression.All:
                    Error("GROUP BY ALL is not allowed on host data sources.");
                    break;
            }

            WalkNode(select.Having, scope, ctes, depth + 1);
            WalkNode(select.NamedWindow, scope, ctes, depth + 1);

            return (outputs, scope);
        }

        private OutputColumn AnalyzeSelectItem(
            SelectItem item,
            Scope scope,
            ImmutableDictionary<string, IReadOnlyList<OutputColumn>> ctes,
            int depth)
        {
            switch (item)
            {
                case SelectItem.UnnamedExpression unnamed:
                    return AnalyzeProjection(unnamed.Expression, null, scope, ctes, depth);
                case SelectItem.ExpressionWithAlias aliased:
                    return AnalyzeProjection(aliased.Expression, Normalize(aliased.Alias), scope, ctes, depth);
                case SelectItem.Wildcard or SelectItem.QualifiedWildcard:
                    WildcardError(scope);
                    return new OutputColumn(null, false);
                default:
                    Error($"Select item '{item.GetType().Name}' is not allowed on host data sources.");
                    return new OutputColumn(null, false);
            }
        }

        private OutputColumn AnalyzeProjection(
            Expression expression,
            string? alias,
            Scope scope,
            ImmutableDictionary<string, IReadOnlyList<OutputColumn>> ctes,
            int depth)
        {
            var reference = PlainColumn(expression);
            if (reference != null)
            {
                // A direct projection is the one place a masked value may leave the query: under its output key, so
                // the result is masked by that key.
                var hit = Resolve(reference, scope, allowMasked: true);

                return new OutputColumn(alias ?? Normalize(reference[^1]), hit.Masked);
            }

            WalkNode(expression, scope, ctes, depth);

            return new OutputColumn(alias, false);
        }

        private void AddTableWithJoins(
            TableWithJoins tableWithJoins,
            Scope scope,
            ImmutableDictionary<string, IReadOnlyList<OutputColumn>> ctes,
            int depth,
            List<Relation> enclosing)
        {
            if (!Enter(depth))
            {
                return;
            }

            // The relations of THIS join tree: an ON / USING clause sees only these (plus outer queries), never the
            // other comma-separated FROM items.
            var joinTree = new List<Relation>();
            AddRelation(tableWithJoins.Relation, scope, ctes, depth + 1, joinTree, lateral: false);

            foreach (var join in tableWithJoins.Joins ?? [])
            {
                var before = joinTree.ToList();
                var apply = join.JoinOperator is JoinOperator.CrossApply or JoinOperator.OuterApply;
                AddRelation(join.Relation, scope, ctes, depth + 1, joinTree, lateral: apply);

                var constraint = join.JoinOperator switch
                {
                    JoinOperator.Inner x => x.JoinConstraint,
                    JoinOperator.LeftOuter x => x.JoinConstraint,
                    JoinOperator.RightOuter x => x.JoinConstraint,
                    JoinOperator.FullOuter x => x.JoinConstraint,
                    JoinOperator.CrossJoin or JoinOperator.CrossApply or JoinOperator.OuterApply => null,
                    _ => UnsupportedJoin(join.JoinOperator)
                };

                ApplyJoinConstraint(constraint, scope, before, joinTree, ctes, depth + 1);
            }

            enclosing.AddRange(joinTree);
        }

        private JoinConstraint? UnsupportedJoin(JoinOperator joinOperator)
        {
            Error($"Join type '{joinOperator.GetType().Name}' is not allowed on host data sources.");
            return null;
        }

        private void ApplyJoinConstraint(
            JoinConstraint? constraint,
            Scope scope,
            List<Relation> left,
            List<Relation> joinTree,
            ImmutableDictionary<string, IReadOnlyList<OutputColumn>> ctes,
            int depth)
        {
            switch (constraint)
            {
                case JoinConstraint.On on:
                    var onScope = new Scope(scope.Parent);
                    onScope.Relations.AddRange(joinTree);
                    onScope.UsingColumns.AddRange(scope.UsingColumns);
                    WalkNode(on.Expression, onScope, ctes, depth);
                    break;
                case JoinConstraint.Using usingConstraint:
                    var right = joinTree[^1];
                    foreach (var ident in usingConstraint.Idents)
                    {
                        var name = Normalize(ident);
                        var rightHit = Lookup(right, name);
                        var leftHits = left
                            .Select(x => Lookup(x, name))
                            .Where(x => x.Found)
                            .ToList();

                        if (!rightHit.Found || leftHits.Count == 0)
                        {
                            Error($"USING column '{ident.Value}' is not exposed by both joined tables.");
                        }
                        else if (rightHit.Masked || leftHits.Any(x => x.Masked))
                        {
                            Error($"Masked column '{ident.Value}' {MaskedUseMessage}.");
                        }

                        scope.UsingColumns.Add(name);
                    }

                    break;
                case JoinConstraint.Natural:
                    Error("NATURAL joins are not allowed on host data sources — they join on every same-named column, including ones Beacon does not expose.");
                    break;
            }
        }

        private void AddRelation(
            TableFactor factor,
            Scope scope,
            ImmutableDictionary<string, IReadOnlyList<OutputColumn>> ctes,
            int depth,
            List<Relation> joinTree,
            bool lateral)
        {
            Relation? relation = null;
            switch (factor)
            {
                case TableFactor.Table table:
                    relation = ResolveTable(table, ctes);
                    break;
                case TableFactor.Derived derived:
                    // Only a LATERAL / APPLY subquery sees the FROM items to its left; otherwise it sees the outer queries.
                    var subqueryOuter = lateral || derived.Lateral ? scope : scope.Parent;
                    var columns = AnalyzeQuery(derived.SubQuery, subqueryOuter, ctes, depth + 1);
                    relation = new Relation
                    {
                        Name = derived.Alias == null ? null : Normalize(derived.Alias.Name),
                        Columns = derived.Alias == null ? columns : Rename(columns, derived.Alias)
                    };
                    break;
                case TableFactor.NestedJoin nested:
                    if (nested.Alias != null)
                    {
                        Error("An alias on a parenthesised join is not allowed on host data sources.");
                    }

                    AddTableWithJoins(nested.TableWithJoins, scope, ctes, depth + 1, joinTree);
                    return;
                default:
                    Error($"Table source '{factor.GetType().Name}' is not allowed on host data sources.");
                    break;
            }

            if (relation != null)
            {
                scope.Relations.Add(relation);
                joinTree.Add(relation);
            }
        }

        private Relation? ResolveTable(TableFactor.Table table, ImmutableDictionary<string, IReadOnlyList<OutputColumn>> ctes)
        {
            var parts = table.Name.Values.ToList();
            var display = string.Join(".", parts.Select(x => x.Value));

            if (table.Args != null)
            {
                Error($"Table-valued function '{display}' is not allowed on host data sources.");
                return null;
            }

            if (IsPresent(table.Partitions) || table.Version != null || table.WithOrdinality)
            {
                Error($"Table options on '{display}' are not allowed on host data sources.");
                return null;
            }

            if (!HintsAreSimple(table.WithHints))
            {
                Error("Only simple table hints (for example NOLOCK) are allowed on host data sources.");
                return null;
            }

            var alias = table.Alias == null ? null : Normalize(table.Alias.Name);

            if (parts.Count == 1 && ctes.TryGetValue(Normalize(parts[0]), out var cteColumns))
            {
                return new Relation
                {
                    Name = alias ?? Normalize(parts[0]),
                    Columns = table.Alias == null ? cteColumns : Rename(cteColumns, table.Alias)
                };
            }

            if (table.Alias is { Columns.Count: > 0 })
            {
                Error($"A column alias list on table '{display}' is not allowed on host data sources.");
                return null;
            }

            if (parts.Count == 1)
            {
                // The connection's default schema / search_path is not necessarily the model's, so an unqualified name
                // could resolve to a different (unexposed) table on the server.
                var candidate = policy.Tables
                    .Where(x => string.Equals(x.Name, parts[0].Value, StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();

                Error(candidate != null
                    ? $"Qualify the table: {QualifiedName(candidate)}. Host data sources require schema-qualified table names."
                    : $"Table '{display}' is not exposed by this host data source (host data sources require schema-qualified table names).");
                return null;
            }

            if (parts.Count > 2)
            {
                // database.schema.table / linked servers leave the exposed database entirely.
                Error($"Table '{display}' is not exposed by this host data source (three-part and linked-server names are not allowed).");
                return null;
            }

            var schema = Normalize(parts[0]);
            var name = Normalize(parts[1]);
            var exposed = policy.Tables
                .Where(x => NameEquals(x.Schema, schema))
                .Where(x => NameEquals(x.Name, name))
                .FirstOrDefault();

            if (exposed == null)
            {
                Error($"Table '{display}' is not exposed by this host data source.");
                return null;
            }

            return new Relation
            {
                Name = alias ?? name,
                Schema = alias == null ? schema : null,
                Table = exposed
            };
        }

        private void ResolveOrderItem(
            Expression expression,
            IReadOnlyList<OutputColumn> outputs,
            Scope scope,
            ImmutableDictionary<string, IReadOnlyList<OutputColumn>> ctes,
            int depth)
        {
            if (Ordinal(expression) is { } ordinal)
            {
                CheckOutputNotMasked(outputs, ordinal);
                return;
            }

            // A bare name in ORDER BY / DISTINCT ON names an output column first (both dialects).
            if (expression is Expression.Identifier { Ident: var ident } && !IsVariable(ident))
            {
                var name = Normalize(ident);
                var matches = outputs
                    .Where(x => x.Name != null && NameEquals(x.Name, name))
                    .ToList();

                if (matches.Count > 0)
                {
                    var input = ResolveUnqualified(ident, scope);
                    if (matches.Any(x => x.Masked) || (input.Found && input.Masked))
                    {
                        Error($"Masked column '{ident.Value}' {MaskedUseMessage}.");
                    }

                    return;
                }
            }

            WalkNode(expression, scope, ctes, depth);
        }

        private void ResolveGroupItem(
            Expression expression,
            IReadOnlyList<OutputColumn> outputs,
            Scope scope,
            ImmutableDictionary<string, IReadOnlyList<OutputColumn>> ctes,
            int depth)
        {
            if (Ordinal(expression) is { } ordinal)
            {
                CheckOutputNotMasked(outputs, ordinal);
                return;
            }

            // A bare name in GROUP BY is an input column; PostgreSQL falls back to an output column name.
            if (_postgres && expression is Expression.Identifier { Ident: var ident } && !ResolveUnqualified(ident, scope).Found)
            {
                var name = Normalize(ident);
                var matches = outputs
                    .Where(x => x.Name != null && NameEquals(x.Name, name))
                    .ToList();

                if (matches.Count > 0)
                {
                    if (matches.Any(x => x.Masked))
                    {
                        Error($"Masked column '{ident.Value}' {MaskedUseMessage}.");
                    }

                    return;
                }
            }

            WalkNode(expression, scope, ctes, depth);
        }

        private void CheckOutputNotMasked(IReadOnlyList<OutputColumn> outputs, int ordinal)
        {
            if (ordinal >= 1 && ordinal <= outputs.Count && outputs[ordinal - 1].Masked)
            {
                Error($"Masked output column {ordinal} {MaskedUseMessage}.");
            }
        }

        // The generic walk: every expression reachable from a clause is resolved in the scope of that clause.
        // Anything not handled explicitly is walked reflectively, so a construct added to the parser later still has
        // its column references and subqueries checked.
        private void WalkNode(
            object? node,
            Scope scope,
            ImmutableDictionary<string, IReadOnlyList<OutputColumn>> ctes,
            int depth)
        {
            if (node == null || node is string || node is Ident || node is ObjectName)
            {
                return;
            }

            if (!Enter(depth))
            {
                return;
            }

            switch (node)
            {
                case Expression.Identifier identifier:
                    VisitIdentifier(identifier.Ident, scope);
                    return;
                case Expression.CompoundIdentifier compound:
                    Resolve(compound.Idents.ToList(), scope, allowMasked: false);
                    return;
                case Expression.Function function:
                    VisitFunction(function, scope, ctes, depth);
                    return;
                case Expression.IsNull isNull when PlainColumn(isNull.Expression) is { } reference:
                    Resolve(reference, scope, allowMasked: true);
                    return;
                case Expression.IsNotNull isNotNull when PlainColumn(isNotNull.Expression) is { } reference:
                    Resolve(reference, scope, allowMasked: true);
                    return;
                case Expression.Exists exists:
                    AnalyzeQuery(exists.SubQuery, scope, ctes, depth + 1);
                    return;
                case Query query:
                    if (AnalyzeQuery(query, scope, ctes, depth + 1).Any(x => x.Masked))
                    {
                        Error($"A subquery returning a masked column cannot be used as a value — a masked column {MaskedUseMessage}.");
                    }

                    return;
                case Expression.LiteralValue { Value: Value.Placeholder }:
                    if (!_postgres)
                    {
                        Error("Placeholders and $-pseudo-columns are not allowed on host data sources.");
                    }

                    return;
                case Expression.Cast cast when _postgres && IsCatalogType(cast.DataType):
                    Error("Casts to catalog types (regclass, regproc …) are not allowed on host data sources.");
                    return;
                case Expression.Wildcard or Expression.QualifiedWildcard or FunctionArgExpression.QualifiedWildcard:
                    WildcardError(scope);
                    return;
                case TableFactor or TableWithJoins or Select or SetExpression or Statement or SelectItem:
                    Error($"'{node.GetType().Name}' is not allowed in that position on host data sources.");
                    return;
            }

            if (node is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    WalkNode(item, scope, ctes, depth + 1);
                }

                return;
            }

            WalkProperties(node, scope, ctes, depth + 1, null);
        }

        private void WalkProperties(
            object node,
            Scope scope,
            ImmutableDictionary<string, IReadOnlyList<OutputColumn>> ctes,
            int depth,
            string? skipProperty)
        {
            foreach (var property in AstProperties(node.GetType()))
            {
                if (property.Name == skipProperty)
                {
                    continue;
                }

                object? value;
                try
                {
                    value = property.GetValue(node);
                }
                catch (TargetInvocationException)
                {
                    continue;
                }

                WalkNode(value, scope, ctes, depth);
            }
        }

        private void VisitIdentifier(Ident ident, Scope scope)
        {
            if (IsVariable(ident))
            {
                if (ident.Value.StartsWith("@@", StringComparison.Ordinal))
                {
                    Error("System variables (@@…) are not allowed on host data sources.");
                    return;
                }

                _parameters.Add(ident.Value[1..]);
                return;
            }

            Resolve([ident], scope, allowMasked: false);
        }

        private void VisitFunction(
            Expression.Function function,
            Scope scope,
            ImmutableDictionary<string, IReadOnlyList<OutputColumn>> ctes,
            int depth)
        {
            var nameParts = function.Name.Values.ToList();
            if (nameParts.Count > 1)
            {
                Error($"Schema-qualified or method-style function '{string.Join(".", nameParts.Select(x => x.Value))}' is not allowed on host data sources.");
                return;
            }

            var name = Normalize(nameParts[0]);
            if (!HostSqlFunctions.IsAllowed(policy.Engine, name, policy.AllowedFunctions))
            {
                Error($"Function '{nameParts[0].Value}' is not on the host data source's function allow-list.");
                return;
            }

            var arguments = function.Args is FunctionArguments.List list ? list.ArgumentList : null;
            var args = arguments?.Args?.ToList() ?? [];

            // COUNT(col) — not COUNT(DISTINCT col), which leaks cardinality — is the one aggregate a masked column may feed.
            if (HostSqlFunctions.IsCount(name)
                && arguments is { DuplicateTreatment: not DuplicateTreatment.Distinct }
                && !IsPresent(arguments.Clauses)
                && args.Count == 1
                && ArgumentExpression(args[0]) is { } countedExpression
                && PlainColumn(countedExpression) is { } counted)
            {
                Resolve(counted, scope, allowMasked: true);
                WalkNode(function.Filter, scope, ctes, depth + 1);
                WalkNode(function.Over, scope, ctes, depth + 1);
                WalkNode(function.WithinGroup, scope, ctes, depth + 1);
                return;
            }

            if (!_postgres
                && DatePartFunctions.Contains(name)
                && args.Count > 0
                && ArgumentExpression(args[0]) is Expression.Identifier { Ident: { QuoteStyle: null } datePart }
                && DateParts.Contains(datePart.Value))
            {
                WalkNode(args.Skip(1).ToList(), scope, ctes, depth + 1);
                WalkNode(arguments!.Clauses, scope, ctes, depth + 1);
                WalkNode(function.Filter, scope, ctes, depth + 1);
                WalkNode(function.Over, scope, ctes, depth + 1);
                WalkNode(function.WithinGroup, scope, ctes, depth + 1);
                return;
            }

            WalkProperties(function, scope, ctes, depth + 1, nameof(Expression.Function.Name));
        }

        private ColumnHit Resolve(List<Ident> reference, Scope scope, bool allowMasked)
        {
            var column = reference[^1];
            var hit = reference.Count == 1
                ? ResolveUnqualified(column, scope)
                : ResolveQualified(reference.Take(reference.Count - 1).ToList(), column, scope);

            if (hit.Error != null)
            {
                Error(hit.Error);
                return hit;
            }

            if (hit.Masked && !allowMasked)
            {
                Error($"Masked column '{string.Join(".", reference.Select(x => x.Value))}' {MaskedUseMessage}.");
            }

            return hit;
        }

        // Unqualified names resolve against the SELECT's own FROM only — never an outer query's (where an unmapped
        // column of an inner table would silently win on the server) and never an output alias.
        private ColumnHit ResolveUnqualified(Ident column, Scope scope)
        {
            var name = Normalize(column);
            var hits = scope.Relations
                .Select(x => Lookup(x, name))
                .Where(x => x.Found)
                .ToList();

            if (hits.Count == 1)
            {
                return hits[0];
            }

            if (hits.Count > 1)
            {
                return scope.UsingColumns.Contains(name, _comparer)
                    ? new ColumnHit(true, hits.Any(x => x.Masked), null)
                    : ColumnHit.Fail($"Column '{column.Value}' is ambiguous — qualify it with its table alias.");
            }

            if (scope.Relations.Any(x => x.Table?.ExcludedColumns.Contains(column.Value) == true))
            {
                return ColumnHit.Fail($"Column '{column.Value}' is not exposed by this host data source.");
            }

            if (Chain(scope).Any(x => x.Relations.Any(y => y.Name != null && NameEquals(y.Name, name))))
            {
                return ColumnHit.Fail($"'{column.Value}' names a table, not a column — whole-row references are not allowed on host data sources.");
            }

            if (Chain(scope).Skip(1).Any(x => x.Relations.Any(y => Lookup(y, name).Found)))
            {
                return ColumnHit.Fail($"Column '{column.Value}' belongs to an outer query — qualify it with that table's alias.");
            }

            return ColumnHit.Fail($"Column '{column.Value}' is not exposed by any table this query reads.");
        }

        private ColumnHit ResolveQualified(List<Ident> qualifier, Ident column, Scope scope)
        {
            var display = $"{string.Join(".", qualifier.Select(x => x.Value))}.{column.Value}";
            if (qualifier.Count > 2)
            {
                return ColumnHit.Fail($"Column '{display}' is not exposed by this host data source (use alias.column).");
            }

            var candidates = Chain(scope)
                .SelectMany(x => x.Relations)
                .Where(x => MatchesQualifier(x, qualifier))
                .ToList();

            if (candidates.Count == 0)
            {
                return ColumnHit.Fail($"Unknown qualifier '{string.Join(".", qualifier.Select(x => x.Value))}' for column '{column.Value}'.");
            }

            // With one relation per name across the whole query, the server can only mean that relation.
            if (candidates.Count > 1)
            {
                return ColumnHit.Fail($"Table alias '{string.Join(".", qualifier.Select(x => x.Value))}' is used more than once in this query — give each table a distinct alias.");
            }

            var hit = Lookup(candidates[0], Normalize(column));

            return hit.Found ? hit : ColumnHit.Fail($"Column '{display}' is not exposed by this host data source.");
        }

        private bool MatchesQualifier(Relation relation, List<Ident> qualifier)
        {
            if (qualifier.Count == 1)
            {
                return relation.Name != null && NameEquals(relation.Name, Normalize(qualifier[0]));
            }

            return relation.Schema != null
                && relation.Name != null
                && NameEquals(relation.Schema, Normalize(qualifier[0]))
                && NameEquals(relation.Name, Normalize(qualifier[1]));
        }

        private ColumnHit Lookup(Relation relation, string name)
        {
            if (relation.Table != null)
            {
                var column = relation.Table.Columns
                    .Where(x => NameEquals(x, name))
                    .FirstOrDefault();

                return column == null
                    ? new ColumnHit(false, false, null)
                    : new ColumnHit(true, relation.Table.MaskedColumns.Contains(column), null);
            }

            var matches = relation.Columns
                .Where(x => x.Name != null && NameEquals(x.Name, name))
                .ToList();

            return new ColumnHit(matches.Count > 0, matches.Any(x => x.Masked), null);
        }

        private IReadOnlyList<OutputColumn> Rename(IReadOnlyList<OutputColumn> columns, TableAlias? alias)
        {
            if (alias?.Columns is not { Count: > 0 } names)
            {
                return columns;
            }

            if (names.Count > columns.Count)
            {
                Error($"The column alias list of '{alias.Name.Value}' names more columns than its query returns.");
            }

            return columns
                .Select((x, i) => i < names.Count ? x with { Name = Normalize(names[i]) } : x)
                .ToList();
        }

        private bool ReferencesTable(object? node, string name, int depth)
        {
            if (node == null || node is string || node is Ident || depth > MaxDepth)
            {
                return false;
            }

            if (node is TableFactor.Table { Name.Values.Count: 1 } table && NameEquals(Normalize(table.Name.Values[0]), name))
            {
                return true;
            }

            if (node is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (ReferencesTable(item, name, depth + 1))
                    {
                        return true;
                    }
                }

                return false;
            }

            foreach (var property in AstProperties(node.GetType()))
            {
                object? value;
                try
                {
                    value = property.GetValue(node);
                }
                catch (TargetInvocationException)
                {
                    continue;
                }

                if (ReferencesTable(value, name, depth + 1))
                {
                    return true;
                }
            }

            return false;
        }

        private void WildcardError(Scope scope)
        {
            var tables = Chain(scope)
                .SelectMany(x => x.Relations)
                .Where(x => x.Table != null)
                .Select(x => x.Table!)
                .Distinct()
                .Select(x => $"{x.Schema}.{x.Name}({string.Join(", ", x.Columns.Order(StringComparer.OrdinalIgnoreCase).Take(40))})");

            Error($"SELECT * / table.* is not allowed on host data sources — list the columns explicitly. Exposed: {string.Join("; ", tables)}.");
        }

        private string QualifiedName(HostExposedTable table)
        {
            if (!_postgres)
            {
                return $"{table.Schema}.{table.Name}";
            }

            return $"{QuotePostgres(table.Schema)}.{QuotePostgres(table.Name)}";
        }

        private static string QuotePostgres(string name)
        {
            return name.All(x => char.IsAsciiLetterLower(x) || char.IsAsciiDigit(x) || x == '_') ? name : $"\"{name}\"";
        }

        private bool IsVariable(Ident ident)
        {
            return !_postgres && ident.QuoteStyle == null && ident.Value.StartsWith('@');
        }

        // A direct column reference (optionally parenthesised): the identifiers, or null when it is anything else.
        private List<Ident>? PlainColumn(Expression expression)
        {
            return expression switch
            {
                Expression.Identifier identifier when !IsVariable(identifier.Ident) => [identifier.Ident],
                Expression.CompoundIdentifier compound => compound.Idents.ToList(),
                Expression.Nested nested => PlainColumn(nested.Expression),
                _ => null
            };
        }

        // PostgreSQL's reg* types (regclass, regproc, regtype …) look names up in the system catalogs.
        private static bool IsCatalogType(DataType dataType)
        {
            return dataType.GetType().Name.StartsWith("Reg", StringComparison.Ordinal)
                || (dataType is DataType.Custom custom
                    && custom.Name.Values.Any(x => x.Value.StartsWith("reg", StringComparison.OrdinalIgnoreCase)));
        }

        private static Expression? ArgumentExpression(FunctionArg argument)
        {
            return argument is FunctionArg.Unnamed { FunctionArgExpression: FunctionArgExpression.FunctionExpression { Expression: var expression } }
                ? expression
                : null;
        }

        private static int? Ordinal(Expression expression)
        {
            return expression is Expression.LiteralValue { Value: Value.Number number } && int.TryParse(number.Value, out var ordinal)
                ? ordinal
                : null;
        }

        private static SetExpression AnchorArm(SetExpression body)
        {
            return body is SetExpression.SetOperation operation ? AnchorArm(operation.Left) : body;
        }

        private static Scope OutputScope(IReadOnlyList<OutputColumn> outputs, Scope? outer)
        {
            var scope = new Scope(outer);
            scope.Relations.Add(new Relation { Name = null, Columns = outputs });

            return scope;
        }

        private static IEnumerable<Scope> Chain(Scope scope)
        {
            for (var current = scope; current != null; current = current.Parent)
            {
                yield return current;
            }
        }

        private static bool HintsAreSimple(IEnumerable<Expression>? hints)
        {
            return (hints ?? []).All(x => x switch
            {
                Expression.Identifier { Ident.QuoteStyle: null } => true,
                Expression.Function function => function.Name.Values.Count == 1 && HintFunctions.Contains(function.Name.Values[0].Value),
                _ => false
            });
        }

        private static bool IsPresent(object? value)
        {
            return value switch
            {
                null => false,
                ICollection collection => collection.Count > 0,
                _ => true
            };
        }

        private bool Enter(int depth)
        {
            if (depth <= MaxDepth)
            {
                return true;
            }

            Error("Query is nested too deeply.");
            return false;
        }

        private void Error(string message)
        {
            _errors.Add(message);
        }

        // PostgreSQL folds unquoted identifiers to lower case and keeps quoted ones verbatim; T-SQL compares
        // case-insensitively under the default collation.
        private string Normalize(Ident ident)
        {
            return _postgres && ident.QuoteStyle == null ? ident.Value.ToLowerInvariant() : ident.Value;
        }

        private bool NameEquals(string left, string right)
        {
            return _comparer.Equals(left, right);
        }
    }

    private static PropertyInfo[] AstProperties(Type type)
    {
        return PropertyCache.GetOrAdd(type, x => x
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(y => y.GetIndexParameters().Length == 0)
            .Where(y => IsAstType(y.PropertyType))
            .ToArray());
    }

    // Walk only into SqlParser AST nodes and sequences of them; primitives, enums, strings and token locations
    // carry no table or column references.
    private static bool IsAstType(Type type)
    {
        if (type == typeof(string) || type.IsPrimitive || type.IsEnum || type.IsValueType)
        {
            return false;
        }

        if (type.Namespace?.StartsWith("SqlParser.Ast", StringComparison.Ordinal) == true)
        {
            return true;
        }

        return typeof(IEnumerable).IsAssignableFrom(type)
            && type.IsGenericType
            && type.GetGenericArguments().All(x => x.Namespace?.StartsWith("SqlParser.Ast", StringComparison.Ordinal) == true
                || (x.IsGenericType && x.GetGenericArguments().All(y => y.Namespace?.StartsWith("SqlParser.Ast", StringComparison.Ordinal) == true)));
    }
}
