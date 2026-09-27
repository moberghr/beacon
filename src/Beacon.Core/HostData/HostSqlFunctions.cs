using Beacon.Core.Data.Enums;

namespace Beacon.Core.HostData;

/// <summary>
/// The per-dialect ALLOW-list of built-in functions SQL against a host data source may call. Everything else —
/// user-defined functions, catalog / settings / file / server-state readers, functions that run a query given as
/// text or turn a whole row into a value (<c>row_to_json</c>, <c>to_json</c>, <c>query_to_xml</c>) — is rejected. A
/// host extends the list for its own vetted functions with <see cref="HostDbContextOptions.AllowFunctions"/>.
/// Syntax forms (<c>CASE</c>, <c>CAST</c> / <c>TRY_CAST</c> / <c>CONVERT</c>, <c>EXTRACT</c>, <c>SUBSTRING … FROM</c>,
/// <c>TRIM</c>, <c>POSITION</c>) are not function calls in the AST and need no entry.
/// </summary>
internal static class HostSqlFunctions
{
    // Lower-case: PostgreSQL folds unquoted function names to lower case, so a quoted name must match exactly.
    private static readonly HashSet<string> PostgreSql = new(StringComparer.Ordinal)
    {
        // Aggregates
        "count", "sum", "avg", "min", "max", "stddev", "stddev_pop", "stddev_samp", "variance", "var_pop", "var_samp",
        "bool_and", "bool_or", "every", "string_agg", "array_agg", "percentile_cont", "percentile_disc", "mode",
        "corr", "covar_pop", "covar_samp", "regr_slope", "regr_intercept", "regr_count", "regr_r2", "regr_avgx", "regr_avgy",
        "json_agg", "jsonb_agg", "json_object_agg", "jsonb_object_agg",

        // Window functions
        "row_number", "rank", "dense_rank", "percent_rank", "cume_dist", "ntile", "lag", "lead",
        "first_value", "last_value", "nth_value",

        // Strings
        "lower", "upper", "length", "char_length", "character_length", "octet_length", "substring", "substr",
        "left", "right", "trim", "ltrim", "rtrim", "btrim", "lpad", "rpad", "replace", "concat", "concat_ws",
        "strpos", "position", "split_part", "initcap", "reverse", "repeat", "starts_with", "translate",
        "regexp_replace", "regexp_match", "regexp_matches", "regexp_substr", "regexp_count", "regexp_like",
        "ascii", "chr", "format", "to_char", "to_number", "to_date", "to_timestamp",

        // Date / time
        "now", "current_date", "current_time", "current_timestamp", "localtime", "localtimestamp",
        "clock_timestamp", "statement_timestamp", "transaction_timestamp", "date_trunc", "date_part", "date_bin",
        "extract", "age", "make_date", "make_time", "make_timestamp", "make_timestamptz", "make_interval",
        "justify_days", "justify_hours", "justify_interval", "isfinite", "timezone",

        // Math
        "abs", "ceil", "ceiling", "floor", "round", "trunc", "mod", "power", "pow", "sqrt", "cbrt", "exp", "ln",
        "log", "log10", "sign", "pi", "div", "width_bucket", "degrees", "radians", "random",

        // Conditional
        "coalesce", "nullif", "greatest", "least", "num_nulls", "num_nonnulls",

        // JSON / arrays on column values (whole-row arguments are rejected by name resolution)
        "json_build_object", "jsonb_build_object", "json_build_array", "jsonb_build_array",
        "json_extract_path", "jsonb_extract_path", "json_extract_path_text", "jsonb_extract_path_text",
        "json_array_length", "jsonb_array_length", "json_typeof", "jsonb_typeof",
        "array_length", "cardinality", "array_to_string"
    };

    private static readonly HashSet<string> SqlServer = new(StringComparer.OrdinalIgnoreCase)
    {
        // Aggregates
        "COUNT", "COUNT_BIG", "SUM", "AVG", "MIN", "MAX", "STDEV", "STDEVP", "VAR", "VARP", "STRING_AGG",
        "GROUPING", "GROUPING_ID", "APPROX_COUNT_DISTINCT",

        // Window functions
        "ROW_NUMBER", "RANK", "DENSE_RANK", "NTILE", "LAG", "LEAD", "FIRST_VALUE", "LAST_VALUE",
        "PERCENT_RANK", "CUME_DIST", "PERCENTILE_CONT", "PERCENTILE_DISC",

        // Strings
        "LEN", "DATALENGTH", "LEFT", "RIGHT", "SUBSTRING", "UPPER", "LOWER", "LTRIM", "RTRIM", "TRIM", "REPLACE",
        "REPLICATE", "REVERSE", "CHARINDEX", "PATINDEX", "CONCAT", "CONCAT_WS", "STUFF", "FORMAT", "SPACE", "STR",
        "TRANSLATE", "ASCII", "CHAR", "UNICODE", "NCHAR",

        // Date / time
        "GETDATE", "GETUTCDATE", "SYSDATETIME", "SYSUTCDATETIME", "SYSDATETIMEOFFSET", "CURRENT_TIMESTAMP",
        "DATEADD", "DATEDIFF", "DATEDIFF_BIG", "DATEPART", "DATENAME", "DATETRUNC", "DATE_BUCKET",
        "DATEFROMPARTS", "DATETIME2FROMPARTS", "DATETIMEFROMPARTS", "DATETIMEOFFSETFROMPARTS",
        "SMALLDATETIMEFROMPARTS", "TIMEFROMPARTS", "EOMONTH", "YEAR", "MONTH", "DAY", "ISDATE",
        "SWITCHOFFSET", "TODATETIMEOFFSET",

        // Math
        "ABS", "CEILING", "FLOOR", "ROUND", "POWER", "SQRT", "SQUARE", "EXP", "LOG", "LOG10", "SIGN", "PI",

        // Conditional / conversion
        "COALESCE", "NULLIF", "ISNULL", "IIF", "CHOOSE", "GREATEST", "LEAST", "PARSE", "TRY_PARSE", "ISNUMERIC",

        // JSON on column values (OPENJSON is a table source and stays rejected)
        "ISJSON", "JSON_VALUE", "JSON_QUERY"
    };

    // Never allowed, not even through AllowFunctions: they read files, settings, catalogs or other servers.
    private static readonly string[] HardDeniedPrefixes = ["pg_", "dblink", "lo_", "xp_", "sp_", "fn_"];

    private static readonly HashSet<string> HardDenied = new(StringComparer.OrdinalIgnoreCase)
    {
        "openrowset", "openquery", "opendatasource", "openxml", "openjson", "current_setting", "set_config",
        "query_to_xml", "query_to_xmlschema", "query_to_xml_and_xmlschema", "table_to_xml", "table_to_xmlschema",
        "table_to_xml_and_xmlschema", "cursor_to_xml", "cursor_to_xmlschema", "schema_to_xml", "schema_to_xmlschema",
        "schema_to_xml_and_xmlschema", "database_to_xml", "database_to_xmlschema", "database_to_xml_and_xmlschema",
        "row_to_json", "to_json", "to_jsonb", "json_populate_record", "jsonb_populate_record",
        "object_definition", "object_name", "object_id", "has_perms_by_name", "serverproperty", "databasepropertyex"
    };

    /// <summary>True for the COUNT aggregates — the only function a masked column may feed.</summary>
    public static bool IsCount(string name)
    {
        return string.Equals(name, "count", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "count_big", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for a name a host may never allow (see <see cref="HostDbContextOptions.AllowFunctions"/>).</summary>
    public static bool IsHardDenied(string name)
    {
        return HardDenied.Contains(name) || HardDeniedPrefixes.Any(x => name.StartsWith(x, StringComparison.OrdinalIgnoreCase));
    }

    /// <param name="name">The function name as the dialect resolves it (PostgreSQL: unquoted folded to lower case).</param>
    public static bool IsAllowed(DatabaseEngineType engine, string name, IReadOnlySet<string> hostAllowed)
    {
        if (IsHardDenied(name))
        {
            return false;
        }

        var builtIns = engine == DatabaseEngineType.PostgreSQL ? PostgreSql : SqlServer;

        return builtIns.Contains(name) || hostAllowed.Contains(name);
    }
}
