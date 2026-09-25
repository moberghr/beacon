using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Validation;
using SqlParser;
using SqlParser.Ast;

namespace Beacon.Core.HostData;

/// <summary>Outcome of a host-policy check. <see cref="MaskedOutputColumns"/> are result keys whose values must be masked.</summary>
public sealed record HostPolicyResult(bool Allowed, string? Error, IReadOnlyList<string> MaskedOutputColumns)
{
    public static HostPolicyResult Reject(string error) => new(false, error, []);
}

/// <summary>
/// Enforces a <see cref="HostExposurePolicy"/> on SQL bound for a host-managed data source. Fail closed:
/// the statement must parse (in the POLICY's dialect, never the caller's) as a single-purpose SELECT, every table it
/// reads must be allow-listed, every column reference must name an exposed column (excluded columns are rejected
/// anywhere they appear), and masked columns may only leave the query as plain projections so their values can be
/// masked by result key. The walk is reflective over the whole AST, so a construct the SqlParserCS visitor skips
/// (derived tables, CTE bodies, function arguments) is still inspected.
/// </summary>
internal static class HostQueryPolicyValidator
{
    private const int MaxDepth = 400;

    // T-SQL date-part functions take a bare keyword (year, month, dd …) as their first argument; the parser reads it
    // as an identifier. Only that first argument is exempt from the column check.
    private static readonly HashSet<string> DatePartFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "DATEPART", "DATEADD", "DATEDIFF", "DATEDIFF_BIG", "DATENAME", "DATETRUNC", "DATE_BUCKET"
    };

    // Functions that can read outside the allow-list (run a query given as text, reach another server, read files
    // or server state). Exact names, plus the prefixes below.
    private static readonly HashSet<string> DeniedFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "openrowset", "openquery", "opendatasource", "openxml", "current_setting", "set_config",
        "query_to_xml", "query_to_xmlschema", "query_to_xml_and_xmlschema",
        "table_to_xml", "table_to_xmlschema", "table_to_xml_and_xmlschema",
        "cursor_to_xml", "cursor_to_xmlschema",
        "schema_to_xml", "schema_to_xmlschema", "schema_to_xml_and_xmlschema",
        "database_to_xml", "database_to_xmlschema", "database_to_xml_and_xmlschema",
        "object_definition", "has_perms_by_name"
    };

    private static readonly string[] DeniedFunctionPrefixes = ["pg_", "dblink", "lo_", "xp_", "sp_", "fn_"];

    // Aggregates whose output carries no column value, so a masked column may appear inside them.
    private static readonly HashSet<string> ValueFreeAggregates = new(StringComparer.OrdinalIgnoreCase)
    {
        "COUNT", "COUNT_BIG", "APPROX_COUNT_DISTINCT"
    };

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();

    public static HostPolicyResult Validate(string sql, HostExposurePolicy policy)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return HostPolicyResult.Reject("Query cannot be empty.");
        }

        Sequence<Statement> statements;
        try
        {
            statements = new Parser().ParseSql(sql, SqlDialects.Resolve(policy.Engine.ToString()));
        }
        catch (Exception ex)
        {
            return HostPolicyResult.Reject($"Host data source queries must parse as {policy.Engine} SQL: {ex.Message}");
        }

        if (statements.Count == 0)
        {
            return HostPolicyResult.Reject("Query cannot be empty.");
        }

        var analyzer = new Analyzer(policy);
        foreach (var statement in statements)
        {
            if (statement is not Statement.Select select)
            {
                return HostPolicyResult.Reject("Only SELECT statements may run against a host data source.");
            }

            analyzer.WalkQuery(select.Query, ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase), 0);
        }

        return analyzer.Finish();
    }

    private sealed class Analyzer(HostExposurePolicy policy)
    {
        private readonly bool _caseSensitive = policy.Engine == DatabaseEngineType.PostgreSQL;
        private readonly List<string> _errors = [];
        private readonly List<HostExposedTable> _referencedTables = [];
        private readonly Dictionary<string, List<HostExposedTable>> _aliasTargets = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _opaqueRelations = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _definedOutputNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string? Qualifier, Ident Column)> _columnRefs = [];
        private readonly HashSet<Ident> _exemptIdents = new(ReferenceEqualityComparer.Instance);
        private readonly List<Query> _queries = [];
        private bool _hasWildcard;
        private bool _hasAliasColumnList;

        public void WalkQuery(Query query, ImmutableHashSet<string> cteScope, int depth)
        {
            if (depth > MaxDepth)
            {
                _errors.Add("Query is nested too deeply.");
                return;
            }

            _queries.Add(query);

            if (query.ForClause != null)
            {
                _errors.Add("FOR JSON / FOR XML / FOR BROWSE clauses are not allowed on host data sources.");
            }

            var scope = cteScope;
            if (query.With != null)
            {
                foreach (var cte in query.With.CteTables)
                {
                    var name = cte.Alias.Name.Value;
                    RegisterAliasColumns(cte.Alias);

                    // A CTE body sees only the CTEs declared BEFORE it (plus itself when recursive). Resolving a
                    // later sibling as a CTE would hide a real table of that name from the allow-list check.
                    var innerScope = query.With.Recursive ? scope.Add(name) : scope;
                    WalkQuery(cte.Query, innerScope, depth + 1);

                    _opaqueRelations.Add(name);
                    scope = scope.Add(name);
                }
            }

            WalkChildren(query, scope, depth + 1, nameof(Query.With));
        }

        public HostPolicyResult Finish()
        {
            if (_errors.Count == 0)
            {
                CheckWildcards();
                CheckColumns();
            }

            var masked = _errors.Count == 0 ? ResolveMaskedOutputs() : [];

            return _errors.Count == 0
                ? new HostPolicyResult(true, null, masked)
                : new HostPolicyResult(false, string.Join(" ", _errors.Distinct()), []);
        }

        private void Walk(object? node, ImmutableHashSet<string> scope, int depth)
        {
            if (node == null || node is string || node is Ident)
            {
                return;
            }

            if (depth > MaxDepth)
            {
                _errors.Add("Query is nested too deeply.");
                return;
            }

            switch (node)
            {
                case Query query:
                    WalkQuery(query, scope, depth + 1);
                    return;
                case TableFactor.Table table:
                    VisitTable(table, scope);
                    return;
                case TableFactor.Derived derived:
                    if (derived.Alias != null)
                    {
                        _opaqueRelations.Add(derived.Alias.Name.Value);
                        RegisterAliasColumns(derived.Alias);
                    }

                    WalkQuery(derived.SubQuery, scope, depth + 1);
                    return;
                case TableFactor.NestedJoin:
                    break;
                case TableFactor other:
                    _errors.Add($"Table source '{other.GetType().Name}' is not allowed on host data sources.");
                    return;
                case Select { Into: not null }:
                    _errors.Add("SELECT INTO is not allowed.");
                    return;
                case SelectItem.Wildcard or SelectItem.QualifiedWildcard or FunctionArgExpression.QualifiedWildcard:
                    _hasWildcard = true;
                    return;
                case SelectItem.ExpressionWithAlias aliased:
                    _definedOutputNames.Add(aliased.Alias.Value);
                    break;
                case Expression.Function function:
                    VisitFunction(function);
                    break;
                case Expression.Identifier identifier:
                    if (!_exemptIdents.Contains(identifier.Ident))
                    {
                        _columnRefs.Add((null, identifier.Ident));
                    }

                    return;
                case Expression.CompoundIdentifier compound:
                    var idents = compound.Idents.ToList();
                    _columnRefs.Add((string.Join(".", idents.Take(idents.Count - 1).Select(x => x.Value)), idents[^1]));
                    return;
                case JoinConstraint.Using usingConstraint:
                    foreach (var ident in usingConstraint.Idents)
                    {
                        _columnRefs.Add((null, ident));
                    }

                    return;
            }

            if (node is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    Walk(item, scope, depth + 1);
                }

                return;
            }

            WalkChildren(node, scope, depth + 1, null);
        }

        private void WalkChildren(object node, ImmutableHashSet<string> scope, int depth, string? skipProperty)
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

                Walk(value, scope, depth);
            }
        }

        private void VisitTable(TableFactor.Table table, ImmutableHashSet<string> scope)
        {
            var parts = table.Name.Values.ToList();

            if (table.Args != null)
            {
                _errors.Add($"Table-valued function '{string.Join(".", parts.Select(x => x.Value))}' is not allowed on host data sources.");
                return;
            }

            if (parts.Count == 1 && scope.Contains(parts[0].Value))
            {
                if (table.Alias != null)
                {
                    _opaqueRelations.Add(table.Alias.Name.Value);
                    RegisterAliasColumns(table.Alias);
                }

                return;
            }

            var exposed = ResolveTable(parts);
            if (exposed == null)
            {
                _errors.Add($"Table '{string.Join(".", parts.Select(x => x.Value))}' is not exposed by this host data source.");
                return;
            }

            _referencedTables.Add(exposed);
            AddAliasTarget(parts[^1].Value, exposed);
            AddAliasTarget(string.Join(".", parts.Select(x => x.Value)), exposed);
            if (table.Alias != null)
            {
                AddAliasTarget(table.Alias.Name.Value, exposed);
                RegisterAliasColumns(table.Alias);
            }
        }

        private void VisitFunction(Expression.Function function)
        {
            var nameParts = function.Name.Values.ToList();
            var name = nameParts[^1].Value;

            if (nameParts.Count > 1)
            {
                _errors.Add($"Schema-qualified function '{string.Join(".", nameParts.Select(x => x.Value))}' is not allowed on host data sources.");
                return;
            }

            if (DeniedFunctions.Contains(name) || DeniedFunctionPrefixes.Any(x => name.StartsWith(x, StringComparison.OrdinalIgnoreCase)))
            {
                _errors.Add($"Function '{name}' is not allowed on host data sources.");
                return;
            }

            if (!DatePartFunctions.Contains(name) || function.Args is not FunctionArguments.List list)
            {
                return;
            }

            var first = list.ArgumentList.Args.FirstOrDefault();
            if (first is FunctionArg.Unnamed { FunctionArgExpression: FunctionArgExpression.FunctionExpression { Expression: Expression.Identifier datePart } })
            {
                _exemptIdents.Add(datePart.Ident);
            }
        }

        private void RegisterAliasColumns(TableAlias alias)
        {
            if (alias.Columns is not { Count: > 0 } columns)
            {
                return;
            }

            _hasAliasColumnList = true;
            foreach (var column in columns)
            {
                _definedOutputNames.Add(column.Value);
            }
        }

        private HostExposedTable? ResolveTable(List<Ident> parts)
        {
            if (parts.Count > 2)
            {
                // database.schema.table / linked servers leave the exposed database entirely.
                return null;
            }

            var name = Normalize(parts[^1]);
            var schema = parts.Count == 2 ? Normalize(parts[0]) : policy.DefaultSchema;

            return policy.Tables
                .Where(x => NameEquals(x.Name, name))
                .Where(x => NameEquals(x.Schema, schema))
                .FirstOrDefault();
        }

        private void AddAliasTarget(string alias, HostExposedTable table)
        {
            if (!_aliasTargets.TryGetValue(alias, out var targets))
            {
                targets = [];
                _aliasTargets[alias] = targets;
            }

            if (!targets.Contains(table))
            {
                targets.Add(table);
            }
        }

        private void CheckWildcards()
        {
            if (!_hasWildcard)
            {
                return;
            }

            var tables = _referencedTables
                .Distinct()
                .Select(x => $"{x.Schema}.{x.Name}({string.Join(", ", x.Columns.Order(StringComparer.OrdinalIgnoreCase).Take(40))})");

            _errors.Add($"SELECT * / table.* is not allowed on host data sources — list the columns explicitly. Exposed: {string.Join("; ", tables)}.");
        }

        private void CheckColumns()
        {
            foreach (var (qualifier, ident) in _columnRefs)
            {
                var name = ident.Value;
                if (name.StartsWith('@'))
                {
                    continue; // T-SQL variable / parameter, not a column.
                }

                // Excluded names are refused wherever they appear — projection, predicate, alias, any qualifier.
                if (_referencedTables.Any(x => x.ExcludedColumns.Contains(name)))
                {
                    _errors.Add($"Column '{name}' is not exposed by this host data source.");
                    continue;
                }

                if (qualifier != null && _aliasTargets.TryGetValue(qualifier, out var targets))
                {
                    if (!targets.All(x => ContainsExact(x.Columns, Normalize(ident))))
                    {
                        _errors.Add($"Column '{qualifier}.{name}' is not exposed by this host data source.");
                    }

                    continue;
                }

                if (qualifier != null && !_opaqueRelations.Contains(qualifier))
                {
                    _errors.Add($"Unknown qualifier '{qualifier}' for column '{name}'.");
                    continue;
                }

                if (_referencedTables.Any(x => ContainsExact(x.Columns, Normalize(ident))) || _definedOutputNames.Contains(name))
                {
                    continue;
                }

                _errors.Add($"Column '{name}' is not exposed by any table this query reads.");
            }
        }

        private List<string> ResolveMaskedOutputs()
        {
            var tainted = _referencedTables
                .SelectMany(x => x.MaskedColumns)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (tainted.Count == 0)
            {
                return [];
            }

            if (_hasAliasColumnList)
            {
                _errors.Add("Column alias lists (AS t(a, b)) are not allowed when the query reads masked columns.");
                return [];
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var query in _queries)
                {
                    changed |= PropagateTaint(FlattenArms(query.Body), tainted);
                }
            }

            foreach (var item in _queries.SelectMany(x => FlattenArms(x.Body)).SelectMany(x => x.Projection))
            {
                var expression = ProjectedExpression(item);
                if (expression == null || PlainColumnName(expression) != null)
                {
                    continue;
                }

                var hit = TaintedReference(expression, tainted);
                if (hit != null)
                {
                    _errors.Add($"Masked column '{hit}' may only be selected directly (optionally aliased), not inside an expression.");
                }
            }

            return _errors.Count == 0 ? tainted.Order(StringComparer.OrdinalIgnoreCase).ToList() : [];
        }

        private bool PropagateTaint(List<Select> arms, HashSet<string> tainted)
        {
            if (arms.Count == 0)
            {
                return false;
            }

            var changed = false;
            var width = arms.Max(x => x.Projection.Count);
            for (var i = 0; i < width; i++)
            {
                var position = i;
                var isTainted = arms
                    .Where(x => position < x.Projection.Count)
                    .Select(x => ProjectedExpression(x.Projection[position]))
                    .Select(x => x == null ? null : PlainColumnName(x))
                    .Any(x => x != null && tainted.Contains(x));

                if (!isTainted)
                {
                    continue;
                }

                // Set operations name their output after the FIRST arm — that key is what gets masked.
                if (position >= arms[0].Projection.Count)
                {
                    continue;
                }

                var outputName = OutputName(arms[0].Projection[position]);
                if (outputName == null)
                {
                    _errors.Add("A masked column in a set operation must line up with a named column in the first SELECT.");
                    continue;
                }

                changed |= tainted.Add(outputName);
            }

            return changed;
        }

        private static List<Select> FlattenArms(SetExpression body)
        {
            return body switch
            {
                SetExpression.SelectExpression select => [select.Select],
                SetExpression.SetOperation operation => [.. FlattenArms(operation.Left), .. FlattenArms(operation.Right)],
                SetExpression.QueryExpression nested => FlattenArms(nested.Query.Body),
                _ => []
            };
        }

        private static Expression? ProjectedExpression(SelectItem item)
        {
            return item switch
            {
                SelectItem.UnnamedExpression unnamed => unnamed.Expression,
                SelectItem.ExpressionWithAlias aliased => aliased.Expression,
                _ => null
            };
        }

        private static string? OutputName(SelectItem item)
        {
            return item switch
            {
                SelectItem.ExpressionWithAlias aliased => aliased.Alias.Value,
                SelectItem.UnnamedExpression unnamed => PlainColumnName(unnamed.Expression),
                _ => null
            };
        }

        private static string? PlainColumnName(Expression expression)
        {
            return expression switch
            {
                Expression.Identifier identifier => identifier.Ident.Value,
                Expression.CompoundIdentifier compound => compound.Idents.Last().Value,
                Expression.Nested nested => PlainColumnName(nested.Expression),
                _ => null
            };
        }

        private static string? TaintedReference(object? node, HashSet<string> tainted, int depth = 0)
        {
            if (node == null || node is string || depth > MaxDepth)
            {
                return null;
            }

            switch (node)
            {
                case Expression.Identifier identifier:
                    return tainted.Contains(identifier.Ident.Value) ? identifier.Ident.Value : null;
                case Expression.CompoundIdentifier compound:
                    var last = compound.Idents.Last().Value;
                    return tainted.Contains(last) ? last : null;
                case Expression.Function function when ValueFreeAggregates.Contains(function.Name.Values.Last().Value):
                    return null;
                case Ident:
                    return null;
            }

            if (node is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    var hit = TaintedReference(item, tainted, depth + 1);
                    if (hit != null)
                    {
                        return hit;
                    }
                }

                return null;
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

                var hit = TaintedReference(value, tainted, depth + 1);
                if (hit != null)
                {
                    return hit;
                }
            }

            return null;
        }

        // PostgreSQL folds unquoted identifiers to lower case and keeps quoted ones verbatim; T-SQL compares
        // case-insensitively under the default collation.
        private string Normalize(Ident ident)
        {
            return _caseSensitive && ident.QuoteStyle == null ? ident.Value.ToLowerInvariant() : ident.Value;
        }

        private bool NameEquals(string exposed, string candidate)
        {
            return string.Equals(exposed, candidate, _caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }

        private bool ContainsExact(IReadOnlySet<string> columns, string candidate)
        {
            return columns.Any(x => NameEquals(x, candidate));
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
            && type.GetGenericArguments().All(x => x.Namespace?.StartsWith("SqlParser.Ast", StringComparison.Ordinal) == true);
    }
}
