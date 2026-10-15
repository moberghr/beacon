using SqlParser.Dialects;

namespace Beacon.Core.Services.Validation;

/// <summary>
/// Functions a read-only statement may not call: they read or write server files, open connections to other servers or
/// storage, run SQL passed as text, change settings, sequences, indexes or server state, take locks, or sleep. A deny list
/// can only name what is known; the database-level read-only path and a read-only login stay the backstop. The read-only validator refuses
/// them as calls anywhere in the statement and as table sources. Matching ignores case and uses the last name part, so
/// a qualified call (<c>pg_catalog.pg_read_file</c>, <c>master.dbo.xp_dirtree</c>) or a quoted one is refused too.
/// </summary>
internal static class SqlDeniedFunctions
{
    // Cross-connection, remote-execution and large-object functions are refused in every dialect.
    internal static readonly HashSet<string> Everywhere = new(StringComparer.OrdinalIgnoreCase)
    {
        "openquery", "openrowset", "opendatasource", "openxml", "external_query", "read_files", "cloud_files"
    };

    internal static readonly string[] EverywherePrefixes = ["dblink", "lo_", "xp_"];

    internal static readonly HashSet<string> PostgreSql = new(StringComparer.OrdinalIgnoreCase)
    {
        "pg_stat_file", "pg_logdir_ls", "pg_sleep", "pg_sleep_for", "pg_sleep_until", "pg_terminate_backend",
        "pg_cancel_backend", "pg_reload_conf", "pg_rotate_logfile", "pg_switch_wal", "pg_promote", "pg_notify",
        "pg_import_system_collations", "pg_log_backend_memory_contexts", "pg_stat_statements_reset", "set_config",
        "nextval", "setval", "loread", "lowrite", "gin_clean_pending_list",
        "query_to_xml", "query_to_xmlschema", "query_to_xml_and_xmlschema", "cursor_to_xml", "cursor_to_xmlschema",
        "table_to_xml", "table_to_xmlschema", "table_to_xml_and_xmlschema", "schema_to_xml", "schema_to_xmlschema",
        "schema_to_xml_and_xmlschema", "database_to_xml", "database_to_xmlschema", "database_to_xml_and_xmlschema"
    };

    internal static readonly string[] PostgreSqlPrefixes =
    [
        "pg_read_", "pg_ls_", "pg_file_", "pg_advisory", "pg_try_advisory", "pg_stat_reset", "pg_logical_",
        "pg_replication_", "pg_create_", "pg_drop_", "pg_backup_", "pg_start_backup", "pg_stop_backup", "pg_wal_replay_",
        "brin_summarize_", "brin_desummarize_"
    ];

    /// <summary>
    /// PostgreSQL extension functions that run the SQL text they are given — in a background worker (pg_background) or as
    /// the edge query of a routing algorithm (pgRouting) — so no name or read-only rule can read what they run.
    /// <see cref="SqlCrossDatabaseRule"/> refuses them with read-only enforcement off too.
    /// </summary>
    internal static readonly string[] PostgreSqlSqlTextPrefixes = ["pg_background_", "pgr_"];

    internal static readonly HashSet<string> MySql = new(StringComparer.OrdinalIgnoreCase)
    {
        "load_file", "sleep", "benchmark", "get_lock", "release_lock", "release_all_locks", "master_pos_wait",
        "source_pos_wait", "wait_for_executed_gtid_set", "wait_until_sql_thread_after_gtids",
        "sys_exec", "sys_eval", "sys_get", "sys_set", "sys_bineval", "lib_mysqludf_sys_info",
        "service_get_read_locks", "service_get_write_locks", "service_release_locks"
    };

    // Keyring, audit-log, firewall and version-token administration functions change server state.
    internal static readonly string[] MySqlPrefixes = ["keyring_", "audit_log_", "mysql_firewall_", "version_tokens_"];

    internal static readonly HashSet<string> TSql = new(StringComparer.OrdinalIgnoreCase)
    {
        "fn_get_audit_file", "fn_dump_dblog", "dm_os_enumerate_filesystem", "dm_os_file_exists"
    };

    internal static readonly string[] TSqlPrefixes = ["sp_", "fn_xe_", "fn_trace_"];

    internal static readonly HashSet<string> Snowflake = new(StringComparer.OrdinalIgnoreCase)
    {
        "getnextval"
    };

    internal static readonly string[] SnowflakePrefixes = ["system$"];

    internal static readonly HashSet<string> Databricks = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_kafka", "read_kinesis", "read_pubsub", "read_pulsar", "read_statestore", "read_state_metadata",
        "reflect", "try_reflect", "java_method", "secret", "try_secret", "http_request"
    };

    /// <summary>
    /// Databricks functions that reach other servers or streams, or run arbitrary code: <see cref="SqlCrossDatabaseRule"/>
    /// refuses them with read-only enforcement off too.
    /// </summary>
    internal static readonly HashSet<string> DatabricksBeyondTheSource = new(StringComparer.OrdinalIgnoreCase)
    {
        "http_request", "read_kafka", "read_kinesis", "read_pubsub", "read_pulsar", "reflect", "try_reflect", "java_method"
    };

    internal static readonly HashSet<string> Sqlite = new(StringComparer.OrdinalIgnoreCase)
    {
        "load_extension", "readfile", "writefile", "edit", "fts3_tokenizer"
    };

    /// <param name="name">The function's last name part, unquoted.</param>
    public static bool IsDenied(string name, Dialect dialect)
    {
        if (ReachesBeyondTheSource(name))
        {
            return true;
        }

        return dialect switch
        {
            PostgreSqlDialect => PostgreSql.Contains(name) || HasPrefix(name, PostgreSqlPrefixes) || HasPrefix(name, PostgreSqlSqlTextPrefixes),
            MySqlDialect => MySql.Contains(name) || HasPrefix(name, MySqlPrefixes),
            MsSqlDialect => TSql.Contains(name) || HasPrefix(name, TSqlPrefixes),
            SnowflakeDialect => Snowflake.Contains(name) || HasPrefix(name, SnowflakePrefixes),
            DatabricksDialect => Databricks.Contains(name),
            SQLiteDialect => Sqlite.Contains(name),
            _ => false
        };
    }

    /// <summary>
    /// The functions of <see cref="Everywhere"/> and <see cref="EverywherePrefixes"/>: they reach other servers, files or
    /// storage, so <see cref="SqlCrossDatabaseRule"/> refuses them with read-only enforcement off too.
    /// </summary>
    /// <param name="name">The function's last name part, unquoted.</param>
    public static bool ReachesBeyondTheSource(string name)
    {
        return Everywhere.Contains(name) || HasPrefix(name, EverywherePrefixes);
    }

    /// <summary>
    /// <see cref="ReachesBeyondTheSource(string)"/>, plus what reaches beyond the source on <paramref name="dialect"/> only:
    /// Databricks' network, stream and reflection functions.
    /// </summary>
    /// <param name="name">The function's last name part, unquoted.</param>
    public static bool ReachesBeyondTheSource(string name, Dialect dialect)
    {
        return ReachesBeyondTheSource(name) || (dialect is DatabricksDialect && DatabricksBeyondTheSource.Contains(name));
    }

    /// <summary>True for a PostgreSQL extension function that runs SQL text (<see cref="PostgreSqlSqlTextPrefixes"/>).</summary>
    /// <param name="name">The function's last name part, unquoted.</param>
    public static bool RunsSqlText(string name, Dialect dialect)
    {
        return dialect is PostgreSqlDialect && HasPrefix(name, PostgreSqlSqlTextPrefixes);
    }

    private static bool HasPrefix(string name, string[] prefixes)
    {
        return prefixes.Any(x => name.StartsWith(x, StringComparison.OrdinalIgnoreCase));
    }
}
