using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Beacon.Core.HostData;
using Beacon.Core.Services.Validation;
using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;

namespace Beacon.Core.Services.Security;

/// <summary>
/// Masks result rows on the union of the PII columns found in the SQL text (or host-masked columns), the result
/// columns whose own name passes <see cref="IQueryGuardrailService.IsPiiColumn"/>, and — when the executed SQL is
/// passed — the names that SQL gives PII data under another name (§1.6/§1.11):
/// <list type="bullet">
/// <item>an alias over any expression that reads a PII column (<c>email AS contact</c>, <c>lower(c.email) AS e</c>,
/// T-SQL <c>contact = email</c>), at any query level, so a CTE or derived-table alias read back by name stays masked;</item>
/// <item>a set operation's output name (taken from its first arm) at a position any arm fills from a PII column — a
/// wildcard over a CTE or derived table of known columns is expanded so its positions line up;</item>
/// <item>a CTE / derived-table column-list rename (<c>AS d(contact)</c>), a Snowflake <c>* RENAME</c> and a BigQuery
/// <c>* REPLACE</c> of a PII column;</item>
/// <item>a whole-row reference to a base table (<c>row_to_json(c)</c>, <c>to_json(c.*)</c>, <c>OBJECT_CONSTRUCT(*)</c>); a
/// bare name is never one on T-SQL, MySQL or SQLite, which read it as a column only, and on PostgreSQL and DuckDB only
/// when no relation the query can see (its own FROM items, and the enclosing ones for a correlated subquery or LATERAL)
/// is known to have a column of that name; other engines read every collision as a whole row;</item>
/// <item>a JSON document or map read by a literal key whose name is PII (<c>profile->>'email'</c>, <c>p #>> '{a,email}'</c>,
/// <c>p #> ARRAY['email']</c>, <c>JSON_VALUE(profile, '$.email')</c>, Snowflake <c>v:email</c>, <c>m['email']</c>); a key
/// computed by an expression is not followed.</item>
/// </list>
/// An EXISTS or IN (subquery) test yields a boolean, never a value of its subquery, so the subquery does not make its
/// output PII; a scalar subquery's value is its own output and does.
/// Where the resolver cannot prove where PII went, every result column is masked (fail closed): a column list on a base
/// table or table function, a set-operation arm or column-list rename over a wildcard of unknown columns, a table
/// function / UNNEST / LATERAL VIEW / PIVOT / UNPIVOT fed from PII, a VALUES list reading PII, an unaliased computed
/// expression reading PII (<c>SELECT lower(email)</c>, <c>SELECT row_to_json(c)</c> — the engine names it), a T-SQL
/// <c>FOR JSON</c> / <c>FOR XML</c> statement (every row folded into one column), and the table factors the resolver does
/// not model. A plain <c>SELECT *</c> over a base table is not one of them: its result keys are the table's own column
/// names. Nor is a column list on a built-in generator whose arguments read no column (<c>generate_series(1, 10) AS
/// g(n)</c>, <c>UNNEST(ARRAY['a']) AS u(x)</c>); one over a column (<c>UNNEST(c.contacts) AS u(x)</c>) fails closed.
/// <para>Host-masked columns (<see cref="HostResultMasker"/>) are masked in full, as the provider masks
/// them; every other PII column is masked partially by <see cref="IQueryGuardrailService.MaskPiiValues"/>.</para>
/// </summary>
public static class PiiRowMasker
{
    public static List<Dictionary<string, object?>> Mask(
        IQueryGuardrailService guardrail,
        IReadOnlyList<Dictionary<string, object?>> rows,
        IEnumerable<string> sqlPiiColumns,
        bool detectByColumnName,
        IReadOnlyList<string>? customPatterns,
        string? sql = null,
        string? dialect = null,
        IEnumerable<string>? hostMaskedColumns = null)
    {
        var hostMasked = new HashSet<string>(hostMaskedColumns ?? [], StringComparer.OrdinalIgnoreCase);
        var resultColumns = rows
            .SelectMany(x => x.Keys);
        var piiColumns = ResolvePiiColumns(guardrail, resultColumns, sqlPiiColumns.Concat(hostMasked), detectByColumnName, customPatterns, sql, dialect);

        if (piiColumns.Count == 0)
        {
            return rows.ToList();
        }

        var partialColumns = piiColumns
            .Where(x => !hostMasked.Contains(x))
            .ToList();

        var masked = new List<Dictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
        {
            if (!row.Keys.Any(x => piiColumns.Contains(x)))
            {
                masked.Add(row);
                continue;
            }

            masked.Add(MaskHostColumns(guardrail.MaskPiiValues(row, partialColumns), hostMasked));
        }

        return masked;
    }

    /// <summary>
    /// The columns <see cref="Mask"/> masks: <paramref name="sqlPiiColumns"/> plus, when <paramref name="detectByColumnName"/>
    /// is on, every result column that is PII by its own name or by the aliasing of <paramref name="sql"/>. The join
    /// surfaces use it to carry a step's PII columns into the in-memory join query, which reads them back by name.
    /// </summary>
    public static IReadOnlySet<string> ResolvePiiColumns(
        IQueryGuardrailService guardrail,
        IEnumerable<string> resultColumns,
        IEnumerable<string> sqlPiiColumns,
        bool detectByColumnName,
        IReadOnlyList<string>? customPatterns,
        string? sql = null,
        string? dialect = null)
    {
        var piiColumns = new HashSet<string>(sqlPiiColumns, StringComparer.OrdinalIgnoreCase);

        if (!detectByColumnName)
        {
            return piiColumns;
        }

        var aliases = string.IsNullOrWhiteSpace(sql)
            ? ProjectionAliases.None
            : ProjectionAliases.Resolve(sql, dialect, x => piiColumns.Contains(x) || guardrail.IsPiiColumn(x, customPatterns));

        var keys = resultColumns
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var key in keys)
        {
            if (aliases.MaskAll || aliases.Names.Contains(key) || guardrail.IsPiiColumn(key, customPatterns))
            {
                piiColumns.Add(key);
            }
        }

        return piiColumns;
    }

    /// <summary>
    /// The result columns of <paramref name="sql"/> that carry a value of <paramref name="sourcePiiColumns"/> — the PII
    /// columns of the relations it reads by name (the steps of a multi-step query, read back in the in-memory join): a
    /// result column of that name, an alias over any expression that reads one (<c>UPPER(a.email) AS x</c>), and — where
    /// the SQL cannot be traced, as for <see cref="Mask"/> — every result column (fail closed). Lineage only: no column is
    /// PII here by its own name, so it applies whatever the detection setting. With no PII among the sources, none is.
    /// </summary>
    public static IReadOnlySet<string> ResolveLineage(
        IEnumerable<string> resultColumns,
        IEnumerable<string> sourcePiiColumns,
        string sql,
        string? dialect)
    {
        var sources = new HashSet<string>(sourcePiiColumns, StringComparer.OrdinalIgnoreCase);
        var lineage = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (sources.Count == 0)
        {
            return lineage;
        }

        var aliases = ProjectionAliases.Resolve(sql, dialect, sources.Contains);
        foreach (var key in resultColumns)
        {
            if (aliases.MaskAll || aliases.Names.Contains(key) || sources.Contains(key))
            {
                lineage.Add(key);
            }
        }

        return lineage;
    }

    // A host-masked value is replaced in full and null stays null, whatever the value already is (§1.6).
    private static Dictionary<string, object?> MaskHostColumns(Dictionary<string, object?> row, IReadOnlySet<string> hostMasked)
    {
        foreach (var key in row.Keys.ToList())
        {
            if (hostMasked.Contains(key) && row[key] != null)
            {
                row[key] = HostResultMasker.MaskedValue;
            }
        }

        return row;
    }

    /// <summary>
    /// One pass over the parsed SQL — CTEs before the body, FROM before the projection — so a name an inner query gives
    /// PII data is known by the time an outer query reads it back. Relations (CTEs, FROM items) are scoped per query so a
    /// wildcard or whole-row reference resolves to the right relation; the PII names themselves are tracked per
    /// statement, not per scope: a name reused elsewhere in the statement for non-PII data is masked too (over-masking,
    /// never a leak).
    /// </summary>
    private sealed class ProjectionAliases(Func<string, bool> isPii, Dialect? dialect = null)
    {
        private const int MaxDepth = 400;

        // count(*) / COUNT_BIG(*) count rows: their * passes no column value.
        private static readonly HashSet<string> RowCountFunctions = new(StringComparer.OrdinalIgnoreCase) { "count", "count_big" };

        // Functions that read a JSON document by a path or key given after the document: JSON_VALUE(doc, '$.email'),
        // Snowflake GET(doc, 'email'), Spark json_tuple(doc, 'name', 'email') (every argument after the first counts).
        private static readonly HashSet<string> JsonPathFunctions = new(StringComparer.OrdinalIgnoreCase)
        {
            "json_value",
            "json_query",
            "json_extract",
            "json_extract_scalar",
            "json_extract_path",
            "json_extract_path_text",
            "jsonb_extract_path",
            "jsonb_extract_path_text",
            "jsonb_path_query",
            "jsonb_path_query_array",
            "jsonb_path_query_first",
            "json_extract_array",
            "json_extract_string",
            "json_extract_string_array",
            "json_query_array",
            "json_value_array",
            "json_tuple",
            "get",
            "get_ignore_case",
            "get_json_object",
            "get_path"
        };

        // The JSON operators that read a value by key or path: -> ->> #> #>>. The containment and existence operators
        // (@>, ?) yield a boolean.
        private static readonly HashSet<BinaryOperator> JsonAccessOperators =
        [
            BinaryOperator.Arrow,
            BinaryOperator.LongArrow,
            BinaryOperator.HashArrow,
            BinaryOperator.HashLongArrow
        ];

        // Built-in generators per engine whose output is computed from their arguments alone. An unknown function may read
        // a table, so a column list on it still fails closed.
        private static readonly HashSet<string> PostgreSqlGenerators = new(StringComparer.OrdinalIgnoreCase)
        {
            "generate_series",
            "generate_subscripts",
            "unnest"
        };

        private static readonly HashSet<string> TSqlGenerators = new(StringComparer.OrdinalIgnoreCase)
        {
            "generate_series",
            "string_split"
        };

        // Pure built-in scalars a generator's arguments may call and still read no data. Any other call, a schema-qualified
        // one included, may be a database function that reads a table (unnest(get_contacts())), so it fails closed.
        private static readonly HashSet<string> PureScalarFunctions = new(StringComparer.OrdinalIgnoreCase)
        {
            "abs",
            "ceil",
            "ceiling",
            "coalesce",
            "concat",
            "floor",
            "generate_series",
            "greatest",
            "least",
            "length",
            "lower",
            "nullif",
            "replace",
            "round",
            "substr",
            "substring",
            "trim",
            "upper"
        };

        private static readonly char[] KeyPathSeparators = ['.', ',', '{', '}', '[', ']', '$', '"', '\'', ':', '*', ' '];

        private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();

        private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

        public static ProjectionAliases None { get; } = new(_ => false);

        // Every result column masked: what an alias of a PII column is called cannot be known without the parse.
        private static ProjectionAliases All { get; } = new(_ => true) { MaskAll = true };

        public IReadOnlySet<string> Names => _names;

        public bool MaskAll { get; private set; }

        public static ProjectionAliases Resolve(string sql, string? dialect, Func<string, bool> isPii)
        {
            Sequence<Statement> statements;
            try
            {
                statements = SqlAst.Parse(sql, dialect);
            }
            catch (Exception)
            {
                // Fail closed: SQL that cannot be parsed (a parser gap, or a statement too deep to walk) may rename a PII
                // column to anything, so every result column is masked. Nothing is logged: the parser message can quote
                // the SQL (§1.11).
                return All;
            }

            var resolver = new ProjectionAliases(isPii, SqlDialects.Resolve(dialect));
            foreach (var statement in statements)
            {
                if (statement is Statement.Select select)
                {
                    resolver.Traced(resolver.AnalyzeQuery(select.Query, new Scope(null), 0));

                    // T-SQL FOR JSON / FOR XML folds every column of every row into one column the engine names.
                    resolver.MaskAll |= FoldsRowsIntoOneColumn(select.Query);
                }
            }

            return resolver;
        }

        // A correlated query (a scalar, EXISTS or IN subquery, a LATERAL derived table) sees the columns of the enclosing
        // queries' FROM relations; a CTE body or a plain derived table does not, so a sibling FROM item's column never
        // shadows a relation name there.
        private List<OutputColumn> AnalyzeQuery(Query query, Scope parent, int depth, bool correlated = true)
        {
            if (!Enter(depth))
            {
                return [];
            }

            // A CTE is visible to the later CTEs and the body of its own query only.
            var scope = new Scope(parent, correlated);
            foreach (var cte in query.With?.CteTables ?? [])
            {
                scope.AddCte(cte.Alias?.Name.Value, Traced(Rename(cte.Alias, AnalyzeQuery(cte.Query, scope, depth + 1, correlated: false))));
            }

            var outputs = AnalyzeSetExpression(query.Body, scope, depth + 1);
            if (!FoldsRowsIntoOneColumn(query))
            {
                return outputs;
            }

            // Every column reaches the folded value: it carries PII when any of them does.
            var pii = outputs.Any(x => x.Pii);
            return outputs
                .Select(x => x.Wildcard ? x with { Pii = pii } : Named(x.Name, pii))
                .ToList();
        }

        private List<OutputColumn> AnalyzeSetExpression(SetExpression body, Scope scope, int depth)
        {
            if (!Enter(depth))
            {
                return [];
            }

            return body switch
            {
                SetExpression.SelectExpression select => AnalyzeSelect(select.Select, scope, depth + 1),
                SetExpression.QueryExpression nested => AnalyzeQuery(nested.Query, scope, depth + 1),
                SetExpression.SetOperation operation => Merge(
                    AnalyzeSetExpression(operation.Left, scope, depth + 1),
                    AnalyzeSetExpression(operation.Right, scope, depth + 1)),
                SetExpression.ValuesExpression values => AnalyzeValues(values.Values, scope, depth + 1),
                // TABLE t and anything else: the unknown columns of a relation that can carry PII.
                _ => [Hidden(true)]
            };
        }

        private List<OutputColumn> AnalyzeSelect(Select select, Scope parent, int depth)
        {
            // FROM first: a derived table names its columns before this projection reads them back.
            var scope = new Scope(parent);
            foreach (var table in select.From ?? [])
            {
                AnalyzeTable(table, scope, depth + 1);
            }

            foreach (var view in select.LateralViews ?? [])
            {
                // LATERAL VIEW explode(…) t AS e: the generator's columns take new names.
                MaskAll |= ReferencesPii(view.Expression, scope, depth + 1);
                scope.AddRelation(new Relation(LastName(view.LateralViewName), null, IsFunction: true));
            }

            // `*` is the FROM relations' columns side by side unless a USING / NATURAL join merged some or a lateral view
            // appended its own.
            var sideBySide = select.LateralViews is not { Count: > 0 }
                && (select.From ?? []).All(KeepsColumnsSideBySide);

            var outputs = new List<OutputColumn>();
            foreach (var item in select.Projection)
            {
                switch (item)
                {
                    case SelectItem.ExpressionWithAlias aliased:
                        outputs.Add(Named(aliased.Alias.Value, ReferencesPii(aliased.Expression, scope, depth + 1)));
                        break;
                    case SelectItem.UnnamedExpression unnamed:
                        outputs.Add(Named(ColumnName(unnamed.Expression), ReferencesPii(unnamed.Expression, scope, depth + 1)));
                        break;
                    case SelectItem.Wildcard wildcard:
                        outputs.AddRange(Expand(scope.Relations, sideBySide, wildcard.WildcardAdditionalOptions, scope, depth + 1));
                        break;
                    case SelectItem.QualifiedWildcard qualified:
                        var relation = scope.FindLocalRelation(LastName(qualified.Name));
                        outputs.AddRange(relation == null
                            ? [Hidden(true)]
                            : Expand([relation], true, qualified.WildcardAdditionalOptions, scope, depth + 1));
                        break;
                    default:
                        outputs.Add(Hidden(true));
                        break;
                }
            }

            return outputs;
        }

        private void AnalyzeTable(TableWithJoins? table, Scope scope, int depth)
        {
            if (table == null)
            {
                return;
            }

            AnalyzeRelation(table.Relation, scope, depth + 1);
            foreach (var join in table.Joins ?? [])
            {
                AnalyzeRelation(join.Relation, scope, depth + 1);
            }
        }

        // Registers the relation in the SELECT's scope, in FROM order, under its alias (or its bare name when unaliased).
        private void AnalyzeRelation(TableFactor? relation, Scope scope, int depth)
        {
            if (relation == null || !Enter(depth))
            {
                return;
            }

            var alias = relation.Alias;
            switch (relation)
            {
                case TableFactor.Table { Args: null } table:
                    var tableName = LastName(table.Name);
                    var cte = table.Name.Values.Count == 1 ? scope.FindCte(tableName) : null;
                    if (cte == null)
                    {
                        // A base table: its columns are unknown and any can be PII; a column list renames them out of reach
                        // of the result-key match.
                        MaskAll |= HasColumnList(alias);
                        scope.AddRelation(new Relation(alias?.Name.Value ?? tableName, null, IsFunction: false));
                        break;
                    }

                    scope.AddRelation(new Relation(alias?.Name.Value ?? tableName, Rename(alias, cte), IsFunction: false));
                    break;
                case TableFactor.Table table:
                    AnalyzeTableFunction(alias?.Name.Value ?? LastName(table.Name), table.Args, alias, IsGenerator(table.Name), scope, depth + 1);
                    break;
                case TableFactor.Function function:
                    AnalyzeTableFunction(alias?.Name.Value ?? LastName(function.Name), function.Args, alias, IsGenerator(function.Name), scope, depth + 1);
                    break;
                case TableFactor.UnNest unnest:
                    // UNNEST returns the elements of the arrays it is given, nothing else.
                    AnalyzeTableFunction(alias?.Name.Value, unnest.ArrayExpressions, alias, true, scope, depth + 1);
                    break;
                case TableFactor.Derived derived:
                    var derivedColumns = AnalyzeQuery(derived.SubQuery, scope, depth + 1, correlated: derived.Lateral);
                    scope.AddRelation(new Relation(alias?.Name.Value, Traced(Rename(alias, derivedColumns)), IsFunction: false));
                    break;
                case TableFactor.NestedJoin nested:
                    // (a JOIN b) AS j: j is the join's whole row, and a column list renames positions nothing can follow.
                    AnalyzeTable(nested.TableWithJoins, scope, depth + 1);
                    MaskAll |= HasColumnList(alias);
                    scope.AddRelation(new Relation(alias?.Name.Value, null, IsFunction: false));
                    break;
                case TableFactor.Pivot pivot:
                    // The pivoted columns are named after the IN values and hold the aggregates.
                    AnalyzeRelation(pivot.TableFactor, scope, depth + 1);
                    MaskAll |= HasColumnList(pivot.PivotAlias ?? alias) || ReferencesPii(pivot.AggregateFunctions, scope, depth + 1);
                    scope.AddRelation(new Relation((pivot.PivotAlias ?? alias)?.Name.Value, null, IsFunction: false));
                    break;
                case TableFactor.Unpivot unpivot:
                    // The value column gathers the IN columns under one new name.
                    AnalyzeRelation(unpivot.TableFactor, scope, depth + 1);
                    MaskAll |= HasColumnList(unpivot.PivotAlias ?? alias) || (unpivot.Columns ?? []).Any(x => IsPiiName(x.Value));
                    scope.AddRelation(new Relation((unpivot.PivotAlias ?? alias)?.Name.Value, null, IsFunction: false));
                    break;
                default:
                    // JSON_TABLE, TABLE(…), MATCH_RECOGNIZE, …: their output columns cannot be followed.
                    MaskAll = true;
                    break;
            }
        }

        // A table-valued function (generate_series, unnest, jsonb_each_text, a UDF): its columns are unknown. Arguments
        // that read PII make every output PII, and a column list renames columns the function may fill from a table,
        // unless it is a built-in generator whose arguments read no column (literals, parameters, nested calls of them):
        // its output is then only what those literals give it. A generator over a column (UNNEST(c.contacts)) renames
        // values of a table whose names are unknown, so it fails closed.
        private void AnalyzeTableFunction(string? name, object? arguments, TableAlias? alias, bool generator, Scope scope, int depth)
        {
            var renamesTableValues = HasColumnList(alias) && (!generator || ReadsColumn(arguments, depth + 1));
            MaskAll |= renamesTableValues || ReferencesPii(arguments, scope, depth + 1);
            scope.AddRelation(new Relation(name, null, IsFunction: true));
        }

        // VALUES names its columns itself (column1, …): a PII position there has a name nothing can trace.
        private List<OutputColumn> AnalyzeValues(Values values, Scope scope, int depth)
        {
            var rows = values.Rows ?? [];
            var width = rows.Count == 0 ? 0 : rows[0].Count;
            var outputs = new List<OutputColumn>(width);
            for (var i = 0; i < width; i++)
            {
                var position = i;
                var pii = rows.Any(x => position < x.Count && ReferencesPii(x[position], scope, depth + 1));
                MaskAll |= pii;
                outputs.Add(new OutputColumn(null, pii, Wildcard: false));
            }

            return outputs;
        }

        // `*` / `t.*`: the relations' known columns in order. A relation of unknown columns (a base table, a table
        // function) leaves one hidden entry instead — its result keys are the table's own column names, so a plain
        // top-level wildcard is still masked by result key, but a set operation or a column list over it fails closed.
        private List<OutputColumn> Expand(IReadOnlyList<Relation> relations, bool sideBySide, WildcardAdditionalOptions? options, Scope scope, int depth)
        {
            var plain = options is null or { ILikeOption: null, ExcludeOption: null, ExceptOption: null, RenameOption: null, ReplaceOption: null };
            if (!plain)
            {
                ApplyWildcardRenames(options!, scope, depth + 1);
            }

            if (plain && sideBySide && relations.All(x => x.Columns != null))
            {
                return relations
                    .SelectMany(x => x.Columns!)
                    .ToList();
            }

            return [Hidden(relations.Any(x => x.MayCarryPii))];
        }

        // Snowflake `* RENAME (email AS contact)` and BigQuery `* REPLACE (email AS name)` put a column under a new name.
        private void ApplyWildcardRenames(WildcardAdditionalOptions options, Scope scope, int depth)
        {
            var renames = options.RenameOption switch
            {
                RenameSelectItem.Single single => [single.Name],
                RenameSelectItem.Multiple multiple => (multiple.Columns ?? []).ToList(),
                _ => new List<IdentWithAlias>()
            };

            foreach (var rename in renames)
            {
                if (IsPiiName(rename.Name.Value))
                {
                    _names.Add(rename.Alias.Value);
                }
            }

            foreach (var replace in options.ReplaceOption?.Items ?? [])
            {
                if (ReferencesPii(replace.Expr, scope, depth + 1))
                {
                    _names.Add(replace.Name.Value);
                }
            }
        }

        // A set operation names its outputs after the FIRST arm; PII at a position in any arm makes that name PII.
        private List<OutputColumn> Merge(List<OutputColumn> left, List<OutputColumn> right)
        {
            if (left.Count != right.Count || left.Any(x => x.Wildcard) || right.Any(x => x.Wildcard))
            {
                // A wildcard over unknown columns hides which position is which: fail closed when either arm can carry PII.
                MaskAll |= left.Any(x => x.Pii) || right.Any(x => x.Pii);
                return left;
            }

            var merged = new List<OutputColumn>(left.Count);
            for (var i = 0; i < left.Count; i++)
            {
                var pii = left[i].Pii || right[i].Pii;

                // An unnamed first-arm position (VALUES, an unaliased expression) takes a name nothing can trace.
                MaskAll |= pii && left[i].Name == null;
                merged.Add(Named(left[i].Name, pii));
            }

            return merged;
        }

        // A column list renames a relation's columns by position: WITH x(contact) AS (SELECT email …).
        private List<OutputColumn> Rename(TableAlias? alias, List<OutputColumn> outputs)
        {
            if (alias?.Columns is not { Count: > 0 } columns)
            {
                return outputs;
            }

            if (outputs.Any(x => x.Wildcard))
            {
                // A wildcard over unknown columns hides which position is which: fail closed when the body can carry PII.
                MaskAll |= outputs.Any(x => x.Pii);
                return outputs;
            }

            var renamed = new List<OutputColumn>(outputs.Count);
            for (var i = 0; i < outputs.Count; i++)
            {
                renamed.Add(i < columns.Count ? Named(columns[i].Value, outputs[i].Pii) : outputs[i]);
            }

            return renamed;
        }

        // Outputs whose names reach the result or a reader (the statement, a CTE, a derived table): an unaliased computed
        // expression there takes an engine-generated name (row_to_json, lower, ?column?, Column1) nothing can trace, so
        // fail closed when it reads PII. A subquery inside an expression is exempt — its value flows through the
        // enclosing expression, which is checked on its own.
        private List<OutputColumn> Traced(List<OutputColumn> outputs)
        {
            MaskAll |= outputs.Any(x => x.Pii && x.Name == null && !x.Wildcard);
            return outputs;
        }

        private OutputColumn Named(string? name, bool pii)
        {
            if (pii && name != null)
            {
                _names.Add(name);
            }

            return new OutputColumn(name, pii, Wildcard: false);
        }

        // Every identifier anywhere under the node counts — function arguments, CASE arms, casts, nested subqueries.
        // SqlParserCS's Visitor does not descend into function arguments, so the walk is reflective. With a scope, a
        // name bound to a FROM relation is a whole-row reference and a subquery is analysed in its own scope; without
        // one (the plain mention scan of a subquery), only the identifier names count.
        private bool ReferencesPii(object? node, Scope? scope, int depth)
        {
            if (depth > MaxDepth)
            {
                return true;
            }

            if (node is Expression expression && ReadsPiiKey(expression))
            {
                return true;
            }

            switch (node)
            {
                case null or string or Ident or ObjectName:
                    return false;
                case Expression.Exists:
                    // A boolean: none of the subquery's values reaches the output.
                    return false;
                case Expression.InSubquery inSubquery:
                    // A boolean over the tested operand; the subquery's values do not reach the output.
                    return ReferencesPii(inSubquery.Expression, scope, depth + 1);
                case Expression.Identifier identifier:
                    return IsPiiName(identifier.Ident.Value) || IsPiiBareRow(identifier.Ident.Value, scope);
                case Expression.CompoundIdentifier compound:
                    return compound.Idents.Count > 0 && IsPiiName(compound.Idents[^1].Value);
                case Expression.QualifiedWildcard qualified:
                    return IsPiiRow(LastName(qualified.Name), scope);
                case FunctionArgExpression.QualifiedWildcard qualified:
                    return IsPiiRow(LastName(qualified.Name), scope);
                case FunctionArgExpression.Wildcard or Expression.Wildcard:
                    // A bare * argument passes every FROM relation's whole row: OBJECT_CONSTRUCT(*), struct(*).
                    return scope?.Relations.Any(x => x.WholeRowIsPii) == true;
                case Expression.Function function when IsRowCount(function):
                    return ReferencesPii(function.Filter, scope, depth + 1)
                        || ReferencesPii(function.Over, scope, depth + 1)
                        || ReferencesPii(function.WithinGroup, scope, depth + 1);
                case Query query when scope != null:
                    // Its output (a whole-row read included) or any PII name anywhere in it, as before.
                    return AnalyzeQuery(query, scope, depth + 1).Any(x => x.Pii) || ReferencesPii(query, null, depth + 1);
                case IEnumerable enumerable:
                    foreach (var item in enumerable)
                    {
                        if (ReferencesPii(item, scope, depth + 1))
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
                    // A node that cannot be read cannot be cleared: treat it as reading PII (fail closed).
                    return true;
                }

                if (ReferencesPii(value, scope, depth + 1))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsPiiName(string name)
        {
            return _names.Contains(name) || isPii(name);
        }

        // A literal key read out of a JSON document or map is the value stored under it: PII when the key's name is. Each
        // step of a path counts ('$.contact.email', '{a,email}', ARRAY['contact', 'email']), through a cast or
        // parentheses. Snowflake v:email is a JsonAccess path; a bracket key (m['email']) is a MapAccess or Subscript. A
        // Databricks v:email path does not parse, so its statement masks every column. A key computed by an expression
        // is not followed.
        private bool ReadsPiiKey(Expression expression)
        {
            var keys = expression switch
            {
                Expression.BinaryOp operation when JsonAccessOperators.Contains(operation.Op) => new List<Expression> { operation.Right },
                Expression.Subscript { Key: Subscript.Index index } => new List<Expression> { index.IndexExpression },
                Expression.MapAccess map => map.Keys
                    .Select(x => x.Key)
                    .ToList(),
                Expression.JsonAccess json => (json.Path.Path ?? [])
                    .Select(x => x is JsonPathElement.Bracket bracket ? bracket.Key : null)
                    .OfType<Expression>()
                    .ToList(),
                Expression.Function function when JsonPathFunctions.Contains(LastName(function.Name) ?? "") => PathArguments(function),
                _ => new List<Expression>()
            };

            var dotKeys = expression is Expression.JsonAccess access
                ? (access.Path.Path ?? [])
                    .OfType<JsonPathElement.Dot>()
                    .Select(x => x.Key)
                : Enumerable.Empty<string>();

            return keys
                .SelectMany(LiteralTexts)
                .Concat(dotKeys)
                .Any(IsPiiKey);
        }

        private bool IsPiiKey(string key)
        {
            return IsPiiName(key) || key
                .Split(KeyPathSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(IsPiiName);
        }

        // A bare name read as a value is the whole row of the FROM relation it names (SELECT c FROM customers c) only on
        // the engines that read it so. On PostgreSQL and DuckDB a column wins over a relation name, so a name that a
        // visible relation is known to have as a column is that column; a base table's columns are unknown, so its name
        // stays a whole row. Elsewhere the order is not relied on and a name collision stays a whole row (fail closed).
        private bool IsPiiBareRow(string name, Scope? scope)
        {
            if (!ReadsBareNameAsRow())
            {
                return false;
            }

            var column = PrefersColumnOverRow() && scope?.HasKnownColumn(name) == true;
            return !column && IsPiiRow(name, scope);
        }

        private bool PrefersColumnOverRow()
        {
            return dialect is PostgreSqlDialect or DuckDbDialect;
        }

        // T-SQL, MySQL and SQLite read a bare name as a column only. Every other engine, an unknown one included, may read
        // it as a whole row (fail closed).
        private bool ReadsBareNameAsRow()
        {
            return dialect is not (MsSqlDialect or MySqlDialect or SQLiteDialect);
        }

        // generate_series and the like: what they return is computed from their arguments alone, never read from a table.
        private bool IsGenerator(ObjectName? name)
        {
            var function = LastName(name);
            if (function == null || name!.Values.Count != 1)
            {
                return false;
            }

            return dialect switch
            {
                PostgreSqlDialect => PostgreSqlGenerators.Contains(function),
                MsSqlDialect => TSqlGenerators.Contains(function),
                _ => false
            };
        }

        // Too deep to analyse: mask every result column rather than miss an alias (fail closed).
        private bool Enter(int depth)
        {
            if (depth <= MaxDepth)
            {
                return true;
            }

            MaskAll = true;
            return false;
        }

        // A name bound to a FROM relation read as a value is that relation's whole row (row_to_json(c), SELECT c).
        private static bool IsPiiRow(string? name, Scope? scope)
        {
            return name != null && scope?.FindRelation(name)?.WholeRowIsPii == true;
        }

        private static List<Expression> PathArguments(Expression.Function function)
        {
            if (function.Args is not FunctionArguments.List { ArgumentList.Args: { } args })
            {
                return [];
            }

            return args
                .Skip(1)
                .Select(x => x is FunctionArg.Unnamed { FunctionArgExpression: FunctionArgExpression.FunctionExpression argument } ? argument.Expression : null)
                .OfType<Expression>()
                .ToList();
        }

        // The literal text of a key: a string, through a cast ('email'::text, '{email}'::text[]) or parentheses, and each
        // element of an array of them (ARRAY['contact', 'email']).
        private static IEnumerable<string> LiteralTexts(Expression expression)
        {
            return expression switch
            {
                Expression.LiteralValue literal => StringValue(literal.Value) is { } text ? [text] : [],
                Expression.Cast cast => LiteralTexts(cast.Expression),
                Expression.Nested nested => LiteralTexts(nested.Expression),
                Expression.Array array => (array.Arr.Element ?? [])
                    .SelectMany(LiteralTexts)
                    .ToList(),
                _ => []
            };
        }

        private static string? StringValue(Value value)
        {
            return value switch
            {
                Value.SingleQuotedString x => x.Value,
                Value.DoubleQuotedString x => x.Value,
                Value.NationalStringLiteral x => x.Value,
                Value.EscapedStringLiteral x => x.Value,
                _ => null
            };
        }

        // Whether any column, row, subquery or non-pure function call is read under the node; literals, parameters and pure
        // built-in calls of them read none.
        private static bool ReadsColumn(object? node, int depth)
        {
            if (depth > MaxDepth)
            {
                return true;
            }

            switch (node)
            {
                case null or string or Ident or ObjectName:
                    return false;
                case Expression.Identifier or Expression.CompoundIdentifier or Expression.Wildcard or Expression.QualifiedWildcard
                    or FunctionArgExpression.Wildcard or FunctionArgExpression.QualifiedWildcard or Query:
                    return true;
                case Expression.Function function when function.Name.Values.Count != 1 || !PureScalarFunctions.Contains(LastName(function.Name) ?? ""):
                    return true;
                case IEnumerable enumerable:
                    foreach (var item in enumerable)
                    {
                        if (ReadsColumn(item, depth + 1))
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
                    // A node that cannot be read cannot be cleared (fail closed).
                    return true;
                }

                if (ReadsColumn(value, depth + 1))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool FoldsRowsIntoOneColumn(Query query)
        {
            return query.ForClause is ForClause.Json or ForClause.Xml;
        }

        private static bool IsRowCount(Expression.Function function)
        {
            return RowCountFunctions.Contains(LastName(function.Name) ?? "")
                && function.Args is FunctionArguments.List { ArgumentList.Args: { Count: > 0 } args }
                && args.All(x => x is FunctionArg.Unnamed
                {
                    FunctionArgExpression: FunctionArgExpression.Wildcard or FunctionArgExpression.FunctionExpression { Expression: Expression.Wildcard }
                });
        }

        private static bool HasColumnList(TableAlias? alias)
        {
            return alias?.Columns is { Count: > 0 };
        }

        // USING / NATURAL merge the join columns and a nested join's layout is not tracked: `*` is no longer the
        // relations' columns side by side.
        private static bool KeepsColumnsSideBySide(TableWithJoins table)
        {
            return table.Relation is not TableFactor.NestedJoin
                && (table.Joins ?? []).All(x => x.Relation is not TableFactor.NestedJoin && !MergesJoinColumns(x.JoinOperator));
        }

        private static bool MergesJoinColumns(JoinOperator? joinOperator)
        {
            return joinOperator is JoinOperator.ConstrainedJoinOperator { JoinConstraint: JoinConstraint.Using or JoinConstraint.Natural }
                or JoinOperator.AsOf { Constraint: JoinConstraint.Using or JoinConstraint.Natural };
        }

        private static OutputColumn Hidden(bool pii)
        {
            return new OutputColumn(null, pii, Wildcard: true);
        }

        private static string? LastName(ObjectName? name)
        {
            return name is { Values.Count: > 0 } ? name.Values[^1].Value : null;
        }

        private static string? ColumnName(Expression expression)
        {
            return expression switch
            {
                Expression.Identifier identifier => identifier.Ident.Value,
                Expression.CompoundIdentifier { Idents.Count: > 0 } compound => compound.Idents[^1].Value,
                Expression.Nested nested => ColumnName(nested.Expression),
                _ => null
            };
        }

        private static PropertyInfo[] AstProperties(Type type)
        {
            return PropertyCache.GetOrAdd(type, x => x
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(y => y.GetIndexParameters().Length == 0)
                .Where(y => IsAstType(y.PropertyType))
                .ToArray());
        }

        // Walk only into SqlParser AST nodes and sequences of them; primitives, enums, strings and token locations carry
        // no column references.
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

        /// <summary>
        /// A relation a SELECT reads: its known output columns (a CTE or derived table), or <c>null</c> when they are
        /// unknown (a base table or table function), whose columns may then be PII.
        /// </summary>
        private sealed record Relation(string? Name, List<OutputColumn>? Columns, bool IsFunction)
        {
            public bool MayCarryPii => Columns?.Any(x => x.Pii) ?? true;

            // A table function's whole row only carries what its arguments feed it, and those are checked separately.
            public bool WholeRowIsPii => !IsFunction && MayCarryPii;
        }

        /// <summary>The CTEs of a query and the FROM relations of a SELECT; lookups fall back to the enclosing scopes.</summary>
        private sealed class Scope(Scope? parent, bool seesParentRelations = true)
        {
            private readonly Scope? _parent = parent;

            private readonly bool _seesParentRelations = seesParentRelations;

            private readonly Dictionary<string, List<OutputColumn>> _ctes = new(StringComparer.OrdinalIgnoreCase);

            private readonly List<Relation> _relations = [];

            public IReadOnlyList<Relation> Relations => _relations;

            public void AddCte(string? name, List<OutputColumn> columns)
            {
                if (name != null)
                {
                    _ctes[name] = columns;
                }
            }

            public void AddRelation(Relation relation)
            {
                _relations.Add(relation);
            }

            public List<OutputColumn>? FindCte(string? name)
            {
                for (var scope = this; scope != null && name != null; scope = scope._parent)
                {
                    if (scope._ctes.TryGetValue(name, out var columns))
                    {
                        return columns;
                    }
                }

                return null;
            }

            public Relation? FindLocalRelation(string? name)
            {
                return Match(_relations, name);
            }

            // The SELECT's own FROM first, then the enclosing queries' (a correlated reference).
            public Relation? FindRelation(string name)
            {
                for (var scope = this; scope != null; scope = scope._parent)
                {
                    var relation = Match(scope._relations, name);
                    if (relation != null)
                    {
                        return relation;
                    }
                }

                return null;
            }

            // A relation here or in an enclosing query this one can see (a correlated subquery) whose known columns include
            // the name. A CTE body or a plain derived table stops the walk: its siblings' columns are not visible to it.
            public bool HasKnownColumn(string name)
            {
                for (var scope = this; scope != null; scope = scope._seesParentRelations ? scope._parent : null)
                {
                    var known = scope._relations
                        .Any(x => x.Columns?.Any(y => string.Equals(y.Name, name, StringComparison.OrdinalIgnoreCase)) == true);
                    if (known)
                    {
                        return true;
                    }
                }

                return false;
            }

            // A name bound twice resolves to the binding that can carry PII (fail closed).
            private static Relation? Match(List<Relation> relations, string? name)
            {
                if (name == null)
                {
                    return null;
                }

                var matches = relations
                    .Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                return matches
                    .Where(x => x.WholeRowIsPii)
                    .FirstOrDefault() ?? matches.FirstOrDefault();
            }
        }

        private sealed record OutputColumn(string? Name, bool Pii, bool Wildcard);
    }
}
