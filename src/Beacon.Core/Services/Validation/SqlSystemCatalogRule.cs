using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;

namespace Beacon.Core.Services.Validation;

/// <summary>
/// A statement reads its data source's own tables, never the engine's catalog, with read-only enforcement on or off.
/// <para>
/// System catalog, statistics and query-history relations hold what no table grant or Beacon check covers: values sampled
/// from every row of a table (PostgreSQL <c>pg_stats</c> most-common values and histogram bounds, SQL Server statistics
/// histograms), other sessions' statements and results on the data source's shared login (<c>pg_stat_activity</c>,
/// <c>sys.dm_exec_sql_text</c>, Snowflake <c>QUERY_HISTORY</c> and <c>RESULT_SCAN</c>, BigQuery
/// <c>INFORMATION_SCHEMA.JOBS</c>) and row counts — under column names PII masking cannot recognise. So a relation — a
/// table, view or table function, wherever it appears in the statement, or the table a <c>DESCRIBE</c> names — is refused when a part before its name is a
/// system schema or database (<c>pg_catalog</c> and every <c>pg_*</c> schema, <c>information_schema</c>, T-SQL
/// <c>sys</c>, MySQL <c>mysql</c> / <c>performance_schema</c> / <c>sys</c>, Snowflake's <c>SNOWFLAKE</c> database,
/// BigQuery <c>INFORMATION_SCHEMA</c>), or when its own name is reserved for the catalog: PostgreSQL <c>pg_*</c> in any
/// schema (an unqualified one resolves to <c>pg_catalog</c> first, an extension's statistics views live in any schema),
/// SQL Server's system compatibility views unqualified or in <c>dbo</c> (<c>sysobjects</c>, <c>sysindexes</c>, …, which
/// resolve there) and the system table functions that resolve without <c>sys</c> (<c>fn_dblog</c>, <c>fn_get_sql</c>, …),
/// BigQuery <c>__TABLES__</c> and <c>__TABLES_SUMMARY__</c>, SQLite <c>sqlite_*</c>, <c>pragma_*</c> and <c>dbstat</c>,
/// and the Snowflake table functions that return query, login, task or load history, an earlier statement's result or its
/// operator statistics. PostgreSQL <c>pg_stat*</c> and <c>pg_get_*</c> functions and the comment functions
/// (<c>obj_description</c>, …) are refused in any position: a set-returning function runs in a select list too, and a
/// definition or comment can carry values.
/// </para>
/// <para>
/// On PostgreSQL an unquoted relation named <c>ONLY</c> is refused: the engine reads <c>ONLY t</c> and <c>ONLY (t)</c> as
/// table t, which the parser reads as a table named ONLY (aliased t, or called with t), so the relation that runs could
/// not be checked.
/// </para>
/// <para>
/// Names compare without regard to case. SQL that cannot be parsed, or in a dialect the rule does not know, is refused.
/// Beacon's own catalog reads (metadata extraction, the migration writer) connect outside the gate and are not affected;
/// a source's tables and columns are listed from Beacon's metadata instead.
/// </para>
/// </summary>
public static class SqlSystemCatalogRule
{
    private const string OnlyMessage =
        "ONLY before a table name is not allowed: it is read as a table named ONLY, so the table the engine reads cannot be checked. Quote the name (\"only\") if it is a table.";

    // SQL Server's system compatibility views, which SQL Server resolves unqualified and in dbo (dbo.sysobjects).
    private static readonly HashSet<string> TSqlCompatibilityViews = Names(
        "sysaltfiles", "syscacheobjects", "syscharsets", "syscolumns", "syscomments", "sysconfigures", "sysconstraints",
        "syscurconfigs", "sysdatabases", "sysdepends", "sysdevices", "sysfilegroups", "sysfiles", "sysforeignkeys",
        "sysfulltextcatalogs", "sysindexes", "sysindexkeys", "syslanguages", "syslockinfo", "syslogins", "sysmembers",
        "sysmessages", "sysobjects", "sysoledbusers", "sysopentapes", "sysperfinfo", "syspermissions", "sysprocesses",
        "sysprotects", "sysreferences", "sysremotelogins", "sysservers", "systypes", "sysusers");

    /// <summary>The refusal reason, or null when the statement reads no catalog relation.</summary>
    /// <param name="sql">The statement as it runs.</param>
    /// <param name="dialect">The data source's dialect name (<see cref="DataSourceSqlDialect"/>).</param>
    public static string? Validate(string sql, string? dialect)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return null;
        }

        var parserDialect = SqlDialects.Resolve(dialect);
        var catalog = CatalogOf(parserDialect);
        if (catalog == null)
        {
            return "The SQL dialect of this data source is not known, so the relations its SQL reads cannot be checked.";
        }

        Sequence<Statement> statements;
        try
        {
            statements = SqlAst.Parse(sql, parserDialect);
        }
        catch (SqlAst.TooDeepException)
        {
            return SqlAst.TooDeepMessage;
        }
        catch (Exception)
        {
            return "The SQL could not be parsed, so the relations it reads cannot be checked.";
        }

        foreach (var statement in statements)
        {
            foreach (var node in SqlAst.Nodes(statement))
            {
                var error = node switch
                {
                    Statement.ExplainTable explain => CheckRelation(explain.Name, catalog, isFunction: false),
                    TableFactor.Table table when catalog.OnlyIsKeyword && IsUnquotedOnly(table.Name) => OnlyMessage,
                    TableFactor.Table table => CheckRelation(table.Name, catalog, isFunction: table.Args != null),
                    TableFactor.Function function => CheckRelation(function.Name, catalog, isFunction: true),
                    TableFactor.TableFunction { Expression: Expression.Function function } => CheckRelation(function.Name, catalog, isFunction: true),
                    Expression.Function function when catalog.IsCatalogFunction(LastPartOf(function.Name)) =>
                        $"{LastPartOf(function.Name)} is not allowed: it reads the engine's catalog, statistics or other sessions' activity, not the data source's own tables.",
                    _ => null
                };

                if (error != null)
                {
                    return error;
                }
            }
        }

        return null;
    }

    private static string? CheckRelation(ObjectName name, Catalog catalog, bool isFunction)
    {
        var parts = name.Values;
        if (parts.Count == 0)
        {
            return null;
        }

        var qualifiers = parts
            .Take(parts.Count - 1)
            .Select(x => x.Value);
        var last = parts[^1].Value;
        var inDefaultSchema = parts.Count == 1
            || (catalog.DefaultSchema != null && parts[^2].Value.Equals(catalog.DefaultSchema, StringComparison.OrdinalIgnoreCase));
        var reserved = qualifiers.Any(x => catalog.SystemSchemas.Contains(x) || HasPrefix(x, catalog.SystemSchemaPrefixes))
            || catalog.ReservedNames.Contains(last)
            || HasPrefix(last, catalog.ReservedPrefixes)
            || (inDefaultSchema && catalog.DefaultSchemaNames.Contains(last))
            || (isFunction && catalog.ReservedFunctionNames.Contains(last));

        return reserved
            ? $"{string.Join('.', parts.Select(x => x.Value))} is a system catalog relation: SQL may read the data source's own tables only, not the engine's catalog, statistics or query history. Beacon lists the source's tables and columns from its own metadata."
            : null;
    }

    private static bool IsUnquotedOnly(ObjectName name)
    {
        return name.Values.Count == 1
            && name.Values[0].QuoteStyle == null
            && name.Values[0].Value.Equals("ONLY", StringComparison.OrdinalIgnoreCase);
    }

    private static string LastPartOf(ObjectName name)
    {
        return name.Values.Count > 0 ? name.Values[^1].Value : "";
    }

    private static bool HasPrefix(string name, string[] prefixes)
    {
        return prefixes.Any(x => name.StartsWith(x, StringComparison.OrdinalIgnoreCase));
    }

    private static Catalog? CatalogOf(Dialect dialect)
    {
        return dialect switch
        {
            PostgreSqlDialect => new Catalog
            {
                SystemSchemas = Names("information_schema"),
                SystemSchemaPrefixes = ["pg_"],
                ReservedPrefixes = ["pg_"],
                CatalogFunctionNames = Names("obj_description", "col_description", "shobj_description"),
                CatalogFunctionPrefixes = ["pg_stat", "pg_get_"],
                OnlyIsKeyword = true
            },
            MsSqlDialect => new Catalog
            {
                SystemSchemas = Names("sys", "INFORMATION_SCHEMA"),
                DefaultSchema = "dbo",
                DefaultSchemaNames = TSqlCompatibilityViews,
                ReservedFunctionNames = Names(
                    "fn_dblog", "fn_full_dblog", "fn_get_sql", "fn_virtualfilestats", "fn_my_permissions", "fn_builtin_permissions",
                    "fn_listextendedproperty", "fn_servershareddrives", "fn_virtualservernodes")
            },
            MySqlDialect => new Catalog
            {
                SystemSchemas = Names("information_schema", "mysql", "performance_schema", "sys")
            },
            SQLiteDialect => new Catalog
            {
                ReservedNames = Names("dbstat"),
                ReservedPrefixes = ["sqlite_", "pragma_"]
            },
            SnowflakeDialect => new Catalog
            {
                SystemSchemas = Names("INFORMATION_SCHEMA", "SNOWFLAKE"),
                ReservedFunctionNames = Names(
                    "QUERY_HISTORY", "QUERY_HISTORY_BY_SESSION", "QUERY_HISTORY_BY_USER", "QUERY_HISTORY_BY_WAREHOUSE", "LOGIN_HISTORY",
                    "LOGIN_HISTORY_BY_USER", "TASK_HISTORY", "SERVERLESS_TASK_HISTORY", "COMPLETE_TASK_GRAPHS", "CURRENT_TASK_GRAPHS",
                    "COPY_HISTORY", "VALIDATE", "VALIDATE_PIPE_LOAD", "RESULT_SCAN", "GET_QUERY_OPERATOR_STATS")
            },
            BigQueryDialect => new Catalog
            {
                SystemSchemas = Names("INFORMATION_SCHEMA"),
                ReservedNames = Names("__TABLES__", "__TABLES_SUMMARY__")
            },
            DatabricksDialect => new Catalog
            {
                SystemSchemas = Names("information_schema")
            },
            _ => null
        };
    }

    private static HashSet<string> Names(params string[] names)
    {
        return new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
    }

    private sealed record Catalog
    {
        /// <summary>Schemas and databases that hold the catalog: a relation qualified with one is refused.</summary>
        public HashSet<string> SystemSchemas { get; init; } = Names();

        public string[] SystemSchemaPrefixes { get; init; } = [];

        /// <summary>Relation names that are catalog relations in any schema.</summary>
        public HashSet<string> ReservedNames { get; init; } = Names();

        public string[] ReservedPrefixes { get; init; } = [];

        /// <summary>The schema an unqualified catalog name also resolves in (T-SQL <c>dbo</c>).</summary>
        public string? DefaultSchema { get; init; }

        /// <summary>Relation names that are catalog relations unqualified or in <see cref="DefaultSchema"/>.</summary>
        public HashSet<string> DefaultSchemaNames { get; init; } = Names();

        /// <summary>Table functions, in any schema, that return the catalog, query history or another statement's result.</summary>
        public HashSet<string> ReservedFunctionNames { get; init; } = Names();

        /// <summary>Functions refused in any position, a select list included.</summary>
        public HashSet<string> CatalogFunctionNames { get; init; } = Names();

        public string[] CatalogFunctionPrefixes { get; init; } = [];

        /// <summary>The engine reads an unquoted <c>ONLY</c> before a table name as a keyword (PostgreSQL).</summary>
        public bool OnlyIsKeyword { get; init; }

        public bool IsCatalogFunction(string name)
        {
            return CatalogFunctionNames.Contains(name) || HasPrefix(name, CatalogFunctionPrefixes);
        }
    }
}
