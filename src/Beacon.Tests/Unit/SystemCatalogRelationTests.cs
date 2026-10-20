using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// SQL reads its data source's own tables, never the engine's catalog: system catalog, statistics and query-history
/// relations — values sampled from every row, other sessions' statements and results, row counts — are refused on every
/// dialect by the shared gate, with read-only enforcement on or off. Ordinary tables, and scalar functions that only share
/// a catalog prefix, pass. The in-memory join is not checked.
/// </summary>
[TestFixture]
public class SystemCatalogRelationTests
{
    private const string SystemCatalog = "system_catalog";

    private SqlExecutionGate _gate = null!;

    // A deployment that lets read-only enforcement be switched off (Beacon:Mcp:ForceReadOnly = false).
    private SqlExecutionGate _unlocked = null!;

    [SetUp]
    public void SetUp()
    {
        _gate = TestSqlGate.Create();
        _unlocked = TestSqlGate.Create(deployment: TestSqlGate.Unlocked);
    }

    [TestCase("PostgreSQL", "SELECT most_common_vals, histogram_bounds FROM pg_stats WHERE tablename = 'customers' AND attname = 'email'")]
    [TestCase("PostgreSQL", "SELECT query FROM pg_stat_activity")]
    [TestCase("PostgreSQL", "SELECT * FROM pg_catalog.pg_statistic")]
    [TestCase("PostgreSQL", "SELECT * FROM PG_CATALOG.PG_STATS")]
    [TestCase("PostgreSQL", "SELECT * FROM \"pg_stats\"")]
    [TestCase("PostgreSQL", "SELECT table_name, column_name FROM information_schema.columns")]
    [TestCase("PostgreSQL", "SELECT reltuples FROM pg_class WHERE relname = 'customers'")]
    [TestCase("PostgreSQL", "SELECT query FROM public.pg_stat_statements")]
    [TestCase("PostgreSQL", "SELECT pg_stat_get_activity(NULL)")]
    [TestCase("PostgreSQL", "SELECT pg_get_viewdef(g) FROM generate_series(1, 100) g")]
    [TestCase("PostgreSQL", "SELECT obj_description(g, 'pg_class') FROM generate_series(1, 100) g")]
    [TestCase("PostgreSQL", "SELECT * FROM pg_stat_get_activity(NULL)")]
    [TestCase("PostgreSQL", "SELECT * FROM LATERAL pg_catalog.pg_stat_get_activity(NULL) a")]
    [TestCase("PostgreSQL", "SELECT * FROM ONLY pg_stats")]
    [TestCase("PostgreSQL", "SELECT * FROM ONLY (pg_stats)")]
    [TestCase("PostgreSQL", "SELECT o.id FROM orders o JOIN ONLY pg_stats ON true")]
    [TestCase("PostgreSQL", "WITH s AS (SELECT most_common_vals FROM pg_stats) SELECT * FROM s")]
    [TestCase("PostgreSQL", "SELECT id, (SELECT most_common_vals FROM pg_stats LIMIT 1) FROM orders")]
    [TestCase("PostgreSQL", "SELECT id FROM orders WHERE EXISTS (SELECT 1 FROM pg_stats WHERE 'a@b.c' = ANY(most_common_vals::text::text[]))")]
    [TestCase("MSSQL", "SELECT h.range_high_key FROM sys.stats s CROSS APPLY sys.dm_db_stats_histogram(s.object_id, s.stats_id) h")]
    [TestCase("MSSQL", "SELECT t.text FROM sys.dm_exec_requests r CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t")]
    [TestCase("MSSQL", "SELECT TABLE_NAME, COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS")]
    [TestCase("MSSQL", "SELECT [rows] FROM [sys].[partitions]")]
    [TestCase("MSSQL", "SELECT rowcnt FROM sysindexes")]
    [TestCase("MSSQL", "SELECT name FROM dbo.sysobjects")]
    [TestCase("MSSQL", "SELECT [RowLog Contents 0] FROM fn_dblog(NULL, NULL)")]
    [TestCase("MSSQL", "SELECT * FROM dbo.fn_get_sql(0x01)")]
    [TestCase("AzureSynapse", "SELECT command FROM sys.dm_pdw_exec_requests")]
    [TestCase("Snowflake", "SELECT query_text FROM TABLE(INFORMATION_SCHEMA.QUERY_HISTORY())")]
    [TestCase("Snowflake", "SELECT query_text FROM TABLE(QUERY_HISTORY_BY_USER())")]
    [TestCase("Snowflake", "SELECT * FROM TABLE(RESULT_SCAN(LAST_QUERY_ID(-1)))")]
    [TestCase("Snowflake", "SELECT * FROM TABLE(GET_QUERY_OPERATOR_STATS('01b2c3'))")]
    [TestCase("Snowflake", "SELECT query_text FROM TABLE(SERVERLESS_TASK_HISTORY())")]
    [TestCase("Snowflake", "SELECT row_count FROM INFORMATION_SCHEMA.TABLES")]
    [TestCase("bigquery", "SELECT query FROM `region-us`.INFORMATION_SCHEMA.JOBS")]
    [TestCase("bigquery", "SELECT query FROM `region-us.INFORMATION_SCHEMA.JOBS_BY_PROJECT`")]
    [TestCase("bigquery", "SELECT row_count FROM sales.__TABLES__")]
    [TestCase("databricks", "SELECT * FROM information_schema.columns")]
    [TestCase("SQLite", "SELECT * FROM sqlite_stat4")]
    [TestCase("SQLite", "SELECT * FROM sqlite_master")]
    [TestCase("SQLite", "SELECT * FROM pragma_table_info('customers')")]
    public void Evaluate_CatalogStatisticsAndQueryHistoryRelations_AreBlocked(string dialect, string sql)
    {
        var report = _gate.Evaluate(Request(sql, dialect) with { MaxRows = 10 });

        report.Blocked.Should().BeTrue();
        report.Verdicts.Schema.Code.Should().Be(SystemCatalog);
        report.Verdicts.Schema.Status.Should().Be(SqlGateStatus.Fail);
        report.BlockReason.Should().Be(report.Verdicts.Schema.Message);
        report.Verdicts.RowLimit.Code.Should().Be("not_evaluated");
        report.FinalSql.Should().Be(sql);
    }

    [TestCase("PostgreSQL", "SELECT most_common_vals FROM pg_stats")]
    [TestCase("MSSQL", "SELECT * FROM sys.dm_db_stats_histogram(1, 1)")]
    [TestCase("Snowflake", "SELECT * FROM TABLE(RESULT_SCAN(LAST_QUERY_ID()))")]
    public void Evaluate_WithReadOnlyEnforcementOff_CatalogRelationsAreStillBlocked(string dialect, string sql)
    {
        var report = _unlocked.Evaluate(Request(sql, dialect) with { EnforceReadOnly = false });

        report.Blocked.Should().BeTrue();
        report.Verdicts.ReadOnly.Code.Should().Be("disabled");
        report.Verdicts.Schema.Code.Should().Be(SystemCatalog);
    }

    // The cross-database verdict comes first, so a catalog read qualified with another database is refused as that, and
    // one in the configured database as a catalog read.
    [TestCase("sales", "system_catalog")]
    [TestCase("hr", "cross_database")]
    public void Evaluate_ACatalogReadQualifiedWithADatabase_IsRefused(string configuredDatabase, string code)
    {
        var report = _gate.Evaluate(Request("SELECT name FROM sales.sys.tables", "MSSQL") with { ConfiguredDatabase = configuredDatabase });

        report.Blocked.Should().BeTrue();
        report.Verdicts.Schema.Code.Should().Be(code);
    }

    [Test]
    public void Evaluate_TheInMemoryJoin_IsNotChecked()
    {
        var report = _gate.Evaluate(SqlGateRequest.ForInMemoryJoin("SELECT name FROM sqlite_master", TestSqlGate.DefaultSettings()));

        report.Blocked.Should().BeFalse();
        report.Verdicts.Schema.Code.Should().NotBe(SystemCatalog);
    }

    [TestCase("PostgreSQL", "SELECT pg_typeof(total), pg_size_pretty(1024::bigint), pg_column_size(total) FROM orders")]
    [TestCase("PostgreSQL", "SELECT id FROM orders AS pg_stats")]
    [TestCase("MSSQL", "SELECT id FROM dbo.system_events")]
    [TestCase("Snowflake", "SELECT * FROM sales.query_history")]
    public void Evaluate_TheSourcesOwnTables_Pass(string dialect, string sql)
    {
        var report = _gate.Evaluate(Request(sql, dialect) with { MaxRows = 10 });

        report.Blocked.Should().BeFalse();
        report.Verdicts.Schema.Code.Should().NotBe(SystemCatalog);
    }

    // Forms the gate refuses at an earlier stage (a database qualifier the source is not configured for) or that only the
    // rule sees: it refuses them on its own too.
    [TestCase("MySQL", "SELECT * FROM information_schema.column_statistics")]
    [TestCase("MySQL", "SELECT * FROM mysql.innodb_table_stats")]
    [TestCase("MySQL", "SELECT sql_text FROM performance_schema.events_statements_history")]
    [TestCase("MySQL", "SELECT * FROM sys.statement_analysis")]
    [TestCase("PostgreSQL", "SELECT * FROM analytics.pg_catalog.pg_stats")]
    [TestCase("PostgreSQL", "SELECT * FROM pg_toast.pg_toast_16384")]
    [TestCase("MSSQL", "SELECT * FROM sales.sys.dm_db_partition_stats")]
    [TestCase("MSSQL", "SELECT * FROM sales.INFORMATION_SCHEMA.TABLES")]
    [TestCase("Snowflake", "SELECT query_text FROM SNOWFLAKE.ACCOUNT_USAGE.QUERY_HISTORY")]
    [TestCase("bigquery", "SELECT * FROM `acme-prod`.sales.INFORMATION_SCHEMA.TABLE_STORAGE")]
    [TestCase("bigquery", "SELECT * FROM sales.__TABLES_SUMMARY__")]
    [TestCase("databricks", "SELECT * FROM main.information_schema.columns")]
    [TestCase("SQLite", "SELECT * FROM main.sqlite_master")]
    [TestCase("SQLite", "SELECT * FROM dbstat")]
    [TestCase("MySQL", "DESCRIBE information_schema.columns")]
    [TestCase("databricks", "DESCRIBE information_schema.columns")]
    [TestCase("MySQL", "EXPLAIN performance_schema.events_statements_history")]
    [TestCase("Snowflake", "SELECT * FROM TABLE(VALIDATE(orders, JOB_ID => '_last'))")]
    public void Rule_RefusesCatalogRelations(string dialect, string sql)
    {
        SqlSystemCatalogRule.Validate(sql, dialect).Should().Contain("is a system catalog relation");
    }

    [TestCase("PostgreSQL", "SELECT * FROM ONLY pg_stats", "ONLY before a table name is not allowed")]
    [TestCase("PostgreSQL", "SELECT pg_stat_get_backend_activity(1)", "pg_stat_get_backend_activity is not allowed")]
    public void Rule_RefusesWhatHidesACatalogRead(string dialect, string sql, string expected)
    {
        SqlSystemCatalogRule.Validate(sql, dialect).Should().Contain(expected);
    }

    [TestCase("PostgreSQL", "SELECT o.id, c.email FROM public.orders o JOIN customers c ON c.id = o.customer_id")]
    [TestCase("PostgreSQL", "SELECT * FROM \"only\"")]
    [TestCase("PostgreSQL", "SELECT * FROM stats.daily_totals")]
    [TestCase("PostgreSQL", "SELECT information_schema, sys FROM orders")]
    [TestCase("PostgreSQL", "SELECT * FROM generate_series(1, 3) AS g(n)")]
    [TestCase("MSSQL", "SELECT * FROM dbo.orders WITH (NOLOCK)")]
    [TestCase("MSSQL", "SELECT * FROM sales.sysindexes_archive")]
    [TestCase("MSSQL", "SELECT * FROM erp.syscomments")]
    [TestCase("MSSQL", "SELECT * FROM dbo.fn_OrdersByCustomer(5)")]
    [TestCase("MySQL", "SELECT * FROM sales.orders")]
    [TestCase("MySQL", "SELECT * FROM sys_config")]
    [TestCase("Snowflake", "SELECT value FROM TABLE(FLATTEN(input => PARSE_JSON('[1, 2]')))")]
    [TestCase("Snowflake", "SELECT * FROM TABLE(analytics.query_history_summary('x'))")]
    [TestCase("Snowflake", "SELECT * FROM analytics.account_usage.costs")]
    [TestCase("databricks", "DESCRIBE sales.orders")]
    [TestCase("bigquery", "SELECT * FROM sales.tables")]
    [TestCase("databricks", "SELECT * FROM sales.orders")]
    [TestCase("SQLite", "SELECT * FROM orders")]
    public void Rule_AllowsTheSourcesOwnTables(string dialect, string sql)
    {
        SqlSystemCatalogRule.Validate(sql, dialect).Should().BeNull();
    }

    [Test]
    public void Rule_RefusesWhatItCannotRead()
    {
        SqlSystemCatalogRule.Validate("SELECT FROM WHERE", "PostgreSQL").Should().Contain("could not be parsed");
        SqlSystemCatalogRule.Validate("SELECT * FROM orders", "oracle").Should().Contain("dialect of this data source is not known");
        SqlSystemCatalogRule.Validate("SELECT * FROM orders", null).Should().Contain("dialect of this data source is not known");
    }

    private static SqlGateRequest Request(string sql, string dialect) =>
        SqlGateRequest.FromSettings(sql, dialect, TestSqlGate.DefaultSettings());
}
