using SqlParser.Dialects;

namespace Beacon.Core.Services.Validation;

/// <summary>
/// Functions a read-only statement may not call: they read or write server files, open connections to other servers or
/// storage, run SQL passed as text, change settings or sequences, take locks, or sleep. The read-only validator refuses
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
        "nextval", "setval", "loread", "lowrite",
        "query_to_xml", "query_to_xmlschema", "query_to_xml_and_xmlschema", "cursor_to_xml", "cursor_to_xmlschema",
        "table_to_xml", "table_to_xmlschema", "table_to_xml_and_xmlschema", "schema_to_xml", "schema_to_xmlschema",
        "schema_to_xml_and_xmlschema", "database_to_xml", "database_to_xmlschema", "database_to_xml_and_xmlschema"
    };

    internal static readonly string[] PostgreSqlPrefixes =
    [
        "pg_read_", "pg_ls_", "pg_file_", "pg_advisory", "pg_try_advisory", "pg_stat_reset", "pg_logical_",
        "pg_replication_", "pg_create_", "pg_drop_", "pg_backup_", "pg_start_backup", "pg_stop_backup", "pg_wal_replay_"
    ];

    internal static readonly HashSet<string> MySql = new(StringComparer.OrdinalIgnoreCase)
    {
        "load_file", "sleep", "benchmark", "get_lock", "release_lock", "release_all_locks", "master_pos_wait",
        "source_pos_wait", "wait_for_executed_gtid_set", "wait_until_sql_thread_after_gtids", "sys_exec", "sys_eval"
    };

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

    internal static readonly HashSet<string> Sqlite = new(StringComparer.OrdinalIgnoreCase)
    {
        "load_extension", "readfile", "writefile", "edit", "fts3_tokenizer"
    };

    /// <param name="name">The function's last name part, unquoted.</param>
    public static bool IsDenied(string name, Dialect dialect)
    {
        if (Everywhere.Contains(name) || HasPrefix(name, EverywherePrefixes))
        {
            return true;
        }

        return dialect switch
        {
            PostgreSqlDialect => PostgreSql.Contains(name) || HasPrefix(name, PostgreSqlPrefixes),
            MySqlDialect => MySql.Contains(name),
            MsSqlDialect => TSql.Contains(name) || HasPrefix(name, TSqlPrefixes),
            SnowflakeDialect => Snowflake.Contains(name) || HasPrefix(name, SnowflakePrefixes),
            DatabricksDialect => Databricks.Contains(name),
            SQLiteDialect => Sqlite.Contains(name),
            _ => false
        };
    }

    private static bool HasPrefix(string name, string[] prefixes)
    {
        return prefixes.Any(x => name.StartsWith(x, StringComparison.OrdinalIgnoreCase));
    }
}
