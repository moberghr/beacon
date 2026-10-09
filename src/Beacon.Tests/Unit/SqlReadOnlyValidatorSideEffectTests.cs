using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Beacon.Core.Services.Validation;

namespace Beacon.Tests.Unit;

/// <summary>
/// A single SELECT can still read or write server files, change settings or sequences, end other sessions, sleep or
/// take row locks through the functions and clauses it uses. Those are refused per dialect wherever they appear in the
/// statement, while columns, tables and literals that merely look alike still pass.
/// </summary>
[TestFixture]
public class SqlReadOnlyValidatorSideEffectTests
{
    private SqlReadOnlyAstValidator _validator = null!;

    [SetUp]
    public void SetUp()
    {
        _validator = new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance);
    }

    [TestCase("SELECT pg_read_file('/etc/passwd')", "PostgreSQL")]
    [TestCase("SELECT pg_catalog.pg_read_file('/etc/passwd')", "PostgreSQL")]
    [TestCase("SELECT \"pg_read_file\"('/etc/passwd')", "PostgreSQL")]
    [TestCase("SELECT PG_READ_BINARY_FILE('/etc/passwd')", "PostgreSQL")]
    [TestCase("SELECT * FROM pg_read_file('/etc/passwd')", "PostgreSQL")]
    [TestCase("SELECT pg_ls_dir('.')", "PostgreSQL")]
    [TestCase("SELECT pg_sleep(10)", "PostgreSQL")]
    [TestCase("SELECT lo_import('/etc/passwd')", "PostgreSQL")]
    [TestCase("SELECT lo_export(1, '/tmp/x')", "PostgreSQL")]
    [TestCase("SELECT set_config('default_transaction_read_only', 'off', false)", "PostgreSQL")]
    [TestCase("SELECT nextval('orders_id_seq')", "PostgreSQL")]
    [TestCase("SELECT pg_terminate_backend(1)", "PostgreSQL")]
    [TestCase("SELECT pg_advisory_lock(1)", "PostgreSQL")]
    [TestCase("SELECT query_to_xml('SELECT 1', true, true, '')", "PostgreSQL")]
    [TestCase("SELECT * FROM LATERAL pg_sleep(1)", "PostgreSQL")]
    [TestCase("SELECT LOAD_FILE('/etc/passwd')", "MySQL")]
    [TestCase("SELECT SLEEP(5)", "MySQL")]
    [TestCase("SELECT BENCHMARK(1000000, MD5('a'))", "MariaDB")]
    [TestCase("SELECT GET_LOCK('x', 10)", "MySQL")]
    [TestCase("SELECT SYSTEM$WAIT(5)", "Snowflake")]
    [TestCase("SELECT system$cancel_query('x')", "Snowflake")]
    [TestCase("SELECT reflect('java.lang.Runtime', 'getRuntime')", "databricks")]
    [TestCase("SELECT java_method('java.lang.System', 'getenv')", "databricks")]
    [TestCase("SELECT secret('scope', 'key')", "databricks")]
    [TestCase("SELECT load_extension('x')", "SQLite")]
    [TestCase("SELECT readfile('/etc/passwd')", "SQLite")]
    [TestCase("SELECT `sleep`(1)", "MySQL")]
    [TestCase("SELECT [xp_dirtree]('x')", "MSSQL")]
    [TestCase("SELECT * FROM sys.dm_os_enumerate_filesystem('D:/', '*')", "MSSQL")]
    [TestCase("SELECT table_to_xml('t', true, true, '')", "PostgreSQL")]
    public void Validate_SideEffectFunction_IsRejected(string sql, string dialect)
    {
        _validator.Validate(sql, dialect).Should().StartWith("The function ");
    }

    // Every entry of the denylist, per dialect, as a scalar call: names as written and prefixes with a suffix.
    [TestCaseSource(nameof(EveryDeniedEntry))]
    public void Validate_EveryDeniedEntry_IsRejected(string dialect, string function)
    {
        _validator.Validate($"SELECT {function}(1)", dialect).Should().StartWith($"The function {function} is not allowed");
    }

    private static IEnumerable<TestCaseData> EveryDeniedEntry()
    {
        var dialects = new (string Dialect, IEnumerable<string> Names, IEnumerable<string> Prefixes)[]
        {
            ("PostgreSQL", SqlDeniedFunctions.PostgreSql, SqlDeniedFunctions.PostgreSqlPrefixes),
            ("MySQL", SqlDeniedFunctions.MySql, []),
            ("MSSQL", SqlDeniedFunctions.TSql, SqlDeniedFunctions.TSqlPrefixes),
            ("Snowflake", SqlDeniedFunctions.Snowflake, SqlDeniedFunctions.SnowflakePrefixes),
            ("databricks", SqlDeniedFunctions.Databricks, []),
            ("bigquery", [], []),
            ("SQLite", SqlDeniedFunctions.Sqlite, [])
        };

        foreach (var (dialect, names, prefixes) in dialects)
        {
            var functions = names
                .Concat(SqlDeniedFunctions.Everywhere)
                .Concat(prefixes.Concat(SqlDeniedFunctions.EverywherePrefixes).Select(x => $"{x}probe"));
            foreach (var function in functions)
            {
                yield return new TestCaseData(dialect, function).SetName($"Validate_EveryDeniedEntry_IsRejected({dialect}, {function})");
            }
        }
    }

    // A denied call is found wherever it sits: CASE arms, EXISTS / IN / ANY subqueries, ORDER BY, FILTER, OVER, LIMIT,
    // CTE bodies, JOIN conditions, derived tables and set-operation arms.
    [TestCase("SELECT CASE WHEN x > 0 THEN pg_sleep(1) END FROM t")]
    [TestCase("SELECT * FROM t WHERE EXISTS (SELECT pg_sleep(5))")]
    [TestCase("SELECT * FROM t WHERE x = ANY(SELECT pg_sleep(1))")]
    [TestCase("SELECT * FROM t ORDER BY (SELECT pg_sleep(5))")]
    [TestCase("SELECT count(*) FILTER (WHERE pg_sleep(1) IS NULL) FROM t")]
    [TestCase("SELECT sum(x) OVER (PARTITION BY pg_sleep(1)) FROM t")]
    [TestCase("SELECT * FROM t LIMIT (SELECT pg_sleep(1))")]
    [TestCase("WITH a AS (SELECT pg_sleep(1)) SELECT * FROM a")]
    [TestCase("SELECT * FROM t JOIN u ON pg_sleep(1) IS NULL")]
    [TestCase("SELECT * FROM (SELECT nextval('s')) x")]
    [TestCase("SELECT 1 UNION ALL SELECT lo_export(1, '/tmp/x')")]
    [TestCase("EXPLAIN SELECT pg_sleep(1)")]
    public void Validate_SideEffectFunctionNestedAnywhere_IsRejected(string sql)
    {
        _validator.Validate(sql, "PostgreSQL").Should().StartWith("The function ");
    }

    [TestCase("SELECT * FROM t FOR UPDATE", "PostgreSQL")]
    [TestCase("SELECT * FROM t FOR SHARE", "PostgreSQL")]
    [TestCase("SELECT * FROM (SELECT * FROM t FOR UPDATE) x", "PostgreSQL")]
    [TestCase("SELECT * FROM t WHERE id IN (SELECT id FROM u FOR UPDATE)", "PostgreSQL")]
    [TestCase("SELECT * FROM accounts FOR UPDATE", "MySQL")]
    [TestCase("SELECT * FROM t FOR UPDATE", "Snowflake")]
    public void Validate_RowLockingClause_IsRejected(string sql, string dialect)
    {
        _validator.Validate(sql, dialect).Should().StartWith("Row-locking clauses");
    }

    [TestCase("SELECT seq.NEXTVAL")]
    [TestCase("SELECT db.sch.seq.nextval")]
    [TestCase("SELECT \"seq\".\"NEXTVAL\"")]
    [TestCase("SELECT id, seq.nextval AS n FROM t")]
    public void Validate_SnowflakeSequenceNextValue_IsRejected(string sql)
    {
        _validator.Validate(sql, "Snowflake").Should().StartWith("Sequence NEXTVAL is not allowed");
    }

    [Test]
    public void Validate_SnowflakeGetNextValTableFunction_IsRejected()
    {
        _validator.Validate("SELECT * FROM TABLE(GETNEXTVAL(seq))", "Snowflake").Should().StartWith("The function GETNEXTVAL is not allowed");
    }

    [TestCase("SELECT * FROM t WITH (UPDLOCK)")]
    [TestCase("SELECT * FROM t WITH (XLOCK, ROWLOCK)")]
    [TestCase("SELECT * FROM t WITH (TABLOCKX)")]
    [TestCase("SELECT * FROM t (UPDLOCK)")]
    [TestCase("SELECT * FROM t with (updlock)")]
    [TestCase("SELECT * FROM a INNER JOIN b WITH (UPDLOCK) ON a.id = b.id")]
    public void Validate_TSqlLockingHint_IsRejected(string sql)
    {
        _validator.Validate(sql, "MSSQL").Should().StartWith("Locking table hints");
    }

    // Names and literals that only resemble a denied function are not calls to it.
    [TestCase("SELECT sleep_minutes, nextval_note FROM shifts", "PostgreSQL")]
    [TestCase("SELECT * FROM pg_read_file_log", "PostgreSQL")]
    [TestCase("SELECT 'nextval(1)' AS note, 'pg_sleep(9)' AS other FROM t", "PostgreSQL")]
    [TestCase("SELECT pg_size_pretty(pg_total_relation_size('orders')), currval('s'), current_setting('timezone')", "PostgreSQL")]
    [TestCase("SELECT lower(name), upper(code), count(*) FROM t GROUP BY 1, 2", "PostgreSQL")]
    [TestCase("SELECT sleep FROM schedule", "MySQL")]
    [TestCase("SELECT 'LOAD_FILE(x)' AS s, benchmark_score FROM runs", "MySQL")]
    [TestCase("SELECT * FROM sleep_log", "MySQL")]
    [TestCase("SELECT system_status FROM t", "Snowflake")]
    [TestCase("SELECT * FROM t WITH (NOLOCK)", "MSSQL")]
    [TestCase("SELECT secret_count FROM vault_stats", "databricks")]
    [TestCase("SELECT sleep(1) AS user_function_named_sleep", "PostgreSQL")]
    [TestCase("SELECT pg_sleep(1) AS user_function_named_pg_sleep", "MySQL")]
    [TestCase("SELECT sp_total FROM t", "MSSQL")]
    [TestCase("SELECT sp_foo(1)", "PostgreSQL")]
    [TestCase("SELECT lower(x), local_time(1) FROM t", "PostgreSQL")]
    [TestCase("SELECT lower(x), local_time(1) FROM t", "MySQL")]
    [TestCase("SELECT lower(x), local_time(1) FROM t", "MSSQL")]
    [TestCase("SELECT lower(x), local_time(1) FROM t", "Snowflake")]
    [TestCase("SELECT lower(x), local_time(1) FROM t", "bigquery")]
    [TestCase("SELECT lower(x), local_time(1) FROM t", "databricks")]
    [TestCase("SELECT lower(x), local_time(1) FROM t", "SQLite")]
    [TestCase("SELECT lo_total FROM t", "Snowflake")]
    [TestCase("SELECT nextval_count, t.nextval_count FROM t", "Snowflake")]
    [TestCase("SELECT s.nextval FROM s", "PostgreSQL")]
    [TestCase("SELECT lo_total FROM t", "bigquery")]
    public void Validate_LookAlikes_Pass(string sql, string dialect)
    {
        _validator.Validate(sql, dialect).Should().BeNull();
    }
}
