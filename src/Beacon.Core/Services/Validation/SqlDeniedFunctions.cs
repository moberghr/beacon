using SqlParser.Ast;
using SqlParser.Dialects;

namespace Beacon.Core.Services.Validation;

/// <summary>
/// Functions a read-only statement may not call: they read or write server files, open connections to other servers or
/// storage, run SQL passed as text, change settings, sequences, indexes or server state, take locks, or sleep. A deny list
/// can only name what is known; the database-level read-only path and a read-only login stay the backstop. The read-only validator refuses
/// them as calls anywhere in the statement and as table sources. Matching ignores case and uses the last name part, so
/// a qualified call (<c>pg_catalog.pg_read_file</c>, <c>master.dbo.xp_dirtree</c>) or a quoted one is refused too;
/// a family a warehouse groups under one qualifier (BigQuery <c>ML.</c>, <c>AI.</c>, <c>OBJ.</c>, Snowflake
/// <c>CORTEX.</c>) is matched on the part before the last.
/// <para>
/// BigQuery, Snowflake and Databricks have no database-level read-only session, so on those engines these lists are the
/// control. What sends rows to a model, calls a remote endpoint, reads, lists or hands out access to external storage,
/// stages or files, or returns secrets reaches beyond the source and is refused with read-only enforcement off too
/// (<see cref="ReachesBeyondTheSource(IReadOnlyList{string}, Dialect)"/>). User-defined functions that call out — BigQuery
/// remote functions, Snowflake external functions and UDFs with external access, Databricks UDFs with network access, SQL
/// Server CLR functions — carry the names their owner chose, so no list can recognise them: the data source login must
/// not be granted their use.
/// </para>
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

    /// <summary>
    /// SQL Server procedures that call an HTTP endpoint, send mail, run R or Python code, or create OLE Automation objects
    /// (<c>sp_OA*</c>): <see cref="SqlCrossDatabaseRule"/> refuses them with read-only enforcement off too.
    /// </summary>
    internal static readonly HashSet<string> TSqlBeyondTheSource = new(StringComparer.OrdinalIgnoreCase)
    {
        "sp_invoke_external_rest_endpoint", "sp_send_dbmail", "sp_execute_external_script"
    };

    internal static readonly string[] TSqlBeyondTheSourcePrefixes = ["sp_oa"];

    internal static readonly HashSet<string> Snowflake = new(StringComparer.OrdinalIgnoreCase)
    {
        "getnextval"
    };

    internal static readonly string[] SnowflakePrefixes = ["system$"];

    /// <summary>
    /// Snowflake functions that read, list or hand out URLs to staged and external files, send a notification, or send
    /// rows to a model (Cortex <c>AI_*</c>; <c>SNOWFLAKE.CORTEX.*</c> by its qualifier): <see cref="SqlCrossDatabaseRule"/>
    /// refuses them with read-only enforcement off too.
    /// </summary>
    internal static readonly HashSet<string> SnowflakeBeyondTheSource = new(StringComparer.OrdinalIgnoreCase)
    {
        "get_presigned_url", "build_stage_file_url", "build_scoped_file_url", "get_stage_location", "get_absolute_path",
        "get_relative_path", "infer_schema", "directory", "to_file", "try_to_file", "fl_get_stage_file_url",
        "fl_get_scoped_file_url", "external_table_files", "external_table_file_registration_history",
        "stage_directory_file_registration_history", "auto_refresh_registration_history", "system$send_email",
        "system$send_snowflake_notification"
    };

    internal static readonly string[] SnowflakeBeyondTheSourcePrefixes = ["ai_"];

    internal static readonly HashSet<string> SnowflakeBeyondTheSourceQualifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "cortex"
    };

    internal static readonly HashSet<string> Databricks = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_kafka", "read_kinesis", "read_pubsub", "read_pulsar", "read_statestore", "read_state_metadata",
        "reflect", "try_reflect", "java_method", "secret", "try_secret", "http_request"
    };

    /// <summary>
    /// Databricks functions that reach other servers, streams, model or vector-search endpoints, storage paths or the
    /// secret store, read a pipeline's event log, or run arbitrary code: <see cref="SqlCrossDatabaseRule"/> refuses them with
    /// read-only enforcement off too. Every built-in AI function starts with <c>ai_</c>.
    /// </summary>
    internal static readonly HashSet<string> DatabricksBeyondTheSource = new(StringComparer.OrdinalIgnoreCase)
    {
        "http_request", "read_kafka", "read_kinesis", "read_pubsub", "read_pulsar", "reflect", "try_reflect", "java_method",
        "read_statestore", "read_state_metadata", "cloud_files_state", "vector_search", "remote_query", "event_log",
        "secret", "try_secret", "list_secrets"
    };

    internal static readonly string[] DatabricksBeyondTheSourcePrefixes = ["ai_"];

    /// <summary>
    /// BigQuery families that send rows to a model or another Google service, or hand out access to Cloud Storage objects:
    /// <c>ML.*</c> (its preprocessing helpers included), <c>AI.*</c> and <c>OBJ.*</c>. Refused by qualifier, so a family
    /// member added later is refused too: <see cref="SqlCrossDatabaseRule"/> refuses them with read-only enforcement off too.
    /// </summary>
    internal static readonly HashSet<string> BigQueryBeyondTheSourceQualifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "ml", "ai", "obj"
    };

    /// <summary>BigQuery table functions that read object tables or return signed URLs to their files.</summary>
    internal static readonly HashSet<string> BigQueryBeyondTheSource = new(StringComparer.OrdinalIgnoreCase)
    {
        "external_object_transform"
    };

    internal static readonly HashSet<string> Sqlite = new(StringComparer.OrdinalIgnoreCase)
    {
        "load_extension", "readfile", "writefile", "edit", "fts3_tokenizer"
    };

    /// <summary>
    /// The function's name parts, unquoted. BigQuery reads a quoted path as its parts (<c>`ML.GENERATE_TEXT`</c> is
    /// <c>ML.GENERATE_TEXT</c>), so there a quoted part is split at its dots; elsewhere a quoted part is one name.
    /// </summary>
    public static IReadOnlyList<string> PartsOf(ObjectName name, Dialect dialect)
    {
        var parts = name.Values.Select(x => x.Value);

        return dialect is BigQueryDialect
            ? parts.SelectMany(x => x.Split('.')).ToList()
            : parts.ToList();
    }

    /// <param name="parts">The function's name parts, unquoted (<see cref="PartsOf"/>).</param>
    public static bool IsDenied(IReadOnlyList<string> parts, Dialect dialect)
    {
        if (ReachesBeyondTheSource(parts, dialect))
        {
            return true;
        }

        var name = LastPartOf(parts);

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
    /// SQL Server's HTTP, mail, external-script and OLE Automation procedures, Snowflake's stage, file, notification and
    /// Cortex functions, Databricks' network, stream, AI, storage, secret and reflection functions, and BigQuery's model,
    /// AI and object-access families.
    /// </summary>
    /// <param name="parts">The function's name parts, unquoted (<see cref="PartsOf"/>).</param>
    public static bool ReachesBeyondTheSource(IReadOnlyList<string> parts, Dialect dialect)
    {
        var name = LastPartOf(parts);
        if (ReachesBeyondTheSource(name))
        {
            return true;
        }

        return dialect switch
        {
            MsSqlDialect => TSqlBeyondTheSource.Contains(name) || HasPrefix(name, TSqlBeyondTheSourcePrefixes),
            SnowflakeDialect => SnowflakeBeyondTheSource.Contains(name)
                || HasPrefix(name, SnowflakeBeyondTheSourcePrefixes)
                || HasQualifier(parts, SnowflakeBeyondTheSourceQualifiers),
            DatabricksDialect => DatabricksBeyondTheSource.Contains(name) || HasPrefix(name, DatabricksBeyondTheSourcePrefixes),
            BigQueryDialect => BigQueryBeyondTheSource.Contains(name) || HasQualifier(parts, BigQueryBeyondTheSourceQualifiers),
            _ => false
        };
    }

    /// <summary>
    /// The name a refusal shows: the qualifier and the function for a family refused by its qualifier (<c>ML.PREDICT</c>),
    /// the function alone otherwise.
    /// </summary>
    /// <param name="parts">The function's name parts, unquoted (<see cref="PartsOf"/>).</param>
    public static string DisplayName(IReadOnlyList<string> parts, Dialect dialect)
    {
        var familyQualifiers = dialect switch
        {
            SnowflakeDialect => SnowflakeBeyondTheSourceQualifiers,
            BigQueryDialect => BigQueryBeyondTheSourceQualifiers,
            _ => null
        };

        return familyQualifiers != null && HasQualifier(parts, familyQualifiers)
            ? $"{parts[^2]}.{parts[^1]}"
            : LastPartOf(parts);
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

    private static bool HasQualifier(IReadOnlyList<string> parts, HashSet<string> qualifiers)
    {
        return parts.Count > 1 && qualifiers.Contains(parts[^2]);
    }

    private static string LastPartOf(IReadOnlyList<string> parts)
    {
        return parts.Count > 0 ? parts[^1] : "";
    }
}
