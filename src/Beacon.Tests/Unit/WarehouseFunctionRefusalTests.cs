using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// BigQuery, Snowflake and Databricks have no database-level read-only session, so the function lists are the control
/// there. A call that sends rows to a model, calls an endpoint, reaches stages, files or external storage, or returns
/// secrets is refused wherever it sits in the statement — select list, WHERE, CTE body, subquery, FROM — quoted or in any
/// case, by the read-only validator and, with read-only enforcement off, by the always-on boundary stage. SQL Server's
/// procedures that call out are refused by the boundary stage too. Ordinary analytical functions still pass.
/// </summary>
[TestFixture]
public class WarehouseFunctionRefusalTests
{
    private const string ConfiguredDatabase = "home";

    private static readonly string[] CallPlacements =
    [
        "SELECT {call} AS v FROM t",
        "SELECT id FROM t WHERE {call} IS NOT NULL",
        "WITH c AS (SELECT {call} AS v FROM t) SELECT v FROM c",
        "SELECT id FROM t WHERE id IN (SELECT {call} FROM u)"
    ];

    private static readonly string[] TablePlacements =
    [
        "SELECT * FROM {table}",
        "WITH c AS (SELECT * FROM {table}) SELECT * FROM c",
        "SELECT id FROM t WHERE id IN (SELECT id FROM {table})"
    ];

    // Scalar or aggregate calls, as the engine accepts them (the stage argument of Snowflake's file functions included).
    private static readonly (string Dialect, string Call)[] RefusedCalls =
    [
        ("bigquery", "AI.GENERATE(('Summarise ', body), connection_id => 'us.conn', endpoint => 'gemini-2.0-flash')"),
        ("bigquery", "ai.Generate_Bool(('Is it late? ', body), connection_id => 'us.conn')"),
        ("bigquery", "AI.SCORE(body)"),
        ("bigquery", "ML.GENERATE_TEXT(body)"),
        ("bigquery", "ml.distance([1.0], [2.0])"),
        ("bigquery", "`ML.UNDERSTAND_TEXT`(body)"),
        ("bigquery", "`ai`.`generate`(body)"),
        ("bigquery", "SAFE.AI.GENERATE(body)"),
        ("bigquery", "OBJ.GET_ACCESS_URL(ref, 'r')"),
        ("bigquery", "obj.make_ref(uri, 'us.conn')"),
        ("bigquery", "`x.EXTERNAL_QUERY`('conn', 'SELECT 1')"),
        ("databricks", "ai_query('endpoint', body)"),
        ("databricks", "AI_Summarize(body)"),
        ("databricks", "`ai_classify`(body, array('a', 'b'))"),
        ("databricks", "ai_gen(body)"),
        ("databricks", "secret('scope', 'key')"),
        ("databricks", "TRY_SECRET('scope', 'key')"),
        ("databricks", "http_request(conn => 'c', method => 'GET', path => '/')"),
        ("Snowflake", "GET_PRESIGNED_URL(@stage, 'a.csv', 60)"),
        ("Snowflake", "build_scoped_file_url(@stage, 'a.csv')"),
        ("Snowflake", "Build_Stage_File_Url(@stage, 'a.csv')"),
        ("Snowflake", "GET_STAGE_LOCATION(@stage)"),
        ("Snowflake", "GET_ABSOLUTE_PATH(@stage, 'a.csv')"),
        ("Snowflake", "GET_RELATIVE_PATH(@stage, url)"),
        ("Snowflake", "TO_FILE('@stage', 'a.pdf')"),
        ("Snowflake", "SNOWFLAKE.CORTEX.COMPLETE('model', body)"),
        ("Snowflake", "snowflake.cortex.summarize(body)"),
        ("Snowflake", "\"CORTEX\".\"SENTIMENT\"(body)"),
        ("Snowflake", "AI_COMPLETE('model', body)"),
        ("Snowflake", "ai_classify(body, ['a', 'b'])"),
        ("Snowflake", "\"AI_FILTER\"(body)"),
        ("Snowflake", "SYSTEM$SEND_EMAIL('integration', 'a@example.com', 's', body)")
    ];

    // Table functions and stage or file locations read in FROM.
    private static readonly (string Dialect, string Table)[] RefusedTables =
    [
        ("bigquery", "AI.FORECAST((SELECT * FROM t), data_col => 'x', timestamp_col => 'ts')"),
        ("bigquery", "`ML.PREDICT`((SELECT 1))"),
        ("bigquery", "EXTERNAL_OBJECT_TRANSFORM(objects, ['SIGNED_URL'])"),
        ("databricks", "vector_search(index => 'main.s.idx', query_text => 'x', num_results => 5)"),
        ("databricks", "cloud_files_state('/checkpoint')"),
        ("databricks", "event_log('pipeline-id')"),
        ("databricks", "list_secrets()"),
        ("databricks", "remote_query('conn', database => 'db', query => 'SELECT 1')"),
        ("databricks", "read_statestore('/checkpoint')"),
        ("databricks", "Read_Files('s3://bucket/x')"),
        ("Snowflake", "TABLE(INFER_SCHEMA(LOCATION => '@stage', FILE_FORMAT => 'ff'))"),
        ("Snowflake", "DIRECTORY(@stage)"),
        ("Snowflake", "TABLE(EXTERNAL_TABLE_FILES(TABLE_NAME => 't'))"),
        ("Snowflake", "'@stage/path' (FILE_FORMAT => 'ff')"),
        ("Snowflake", "'@stage'"),
        ("Snowflake", "'@~/exports/'"),
        ("Snowflake", "'@%orders'"),
        ("Snowflake", "\"@stage\""),
        ("Snowflake", "'s3://bucket/x'"),
        ("PostgreSQL", "'orders'"),
        ("MySQL", "'orders'"),
        ("MSSQL", "'orders'"),
        ("bigquery", "'orders'"),
        ("databricks", "'orders'")
    ];

    // Procedures that call out: refused by the boundary stage whether or not read-only enforcement is on.
    private static readonly (string Dialect, string Sql)[] RefusedProcedures =
    [
        ("MSSQL", "EXEC sp_invoke_external_rest_endpoint @url = 'https://example.com'"),
        ("MSSQL", "EXEC dbo.sp_send_dbmail @recipients = 'a@example.com'"),
        ("MSSQL", "EXEC sp_execute_external_script @language = N'Python', @script = N'x'"),
        ("MSSQL", "EXEC sp_OAMethod @o, 'Run'"),
        ("AzureSynapse", "EXEC [sp_invoke_external_rest_endpoint] @url = 'https://example.com'")
    ];

    private SqlReadOnlyAstValidator _validator = null!;

    [SetUp]
    public void SetUp()
    {
        _validator = new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance);
    }

    [TestCaseSource(nameof(RefusedCallStatements))]
    public void Validator_RefusesTheCall(string dialect, string sql)
    {
        _validator.Validate(sql, dialect).Should().StartWith("The function ");
    }

    [TestCaseSource(nameof(RefusedTableStatements))]
    public void Validator_RefusesTheTableSource(string dialect, string sql)
    {
        _validator.Validate(sql, dialect).Should().Match(x => x.StartsWith("The function ") || x == SqlReadOnlyAstValidator.LocationReferenceMessage);
    }

    [TestCaseSource(nameof(EveryRefusedStatement))]
    public void Gate_WithReadOnlyEnforcementOff_StillRefuses(string dialect, string sql)
    {
        var request = SqlGateRequest.FromSettings(sql, dialect, TestSqlGate.DefaultSettings(), TestRowSecurity.NotApplicable) with
        {
            EnforceReadOnly = false,
            ConfiguredDatabase = ConfiguredDatabase
        };

        var report = TestSqlGate.Create(deployment: TestSqlGate.Unlocked).Evaluate(request);

        report.Blocked.Should().BeTrue();
        report.Verdicts.ReadOnly.Status.Should().NotBe(SqlGateStatus.Fail, "read-only enforcement is off; the boundary stage refuses it");
        report.Verdicts.Schema.Code.Should().Be(SqlGateCodes.CrossDatabase);
        report.BlockReason.Should().Match(x => x.Contains("is not allowed: it reaches other servers, files, storage, models or secrets") || x == SqlReadOnlyAstValidator.LocationReferenceMessage);
    }

    [Test]
    public void Validator_NamesTheFamilyOfAFunctionRefusedByItsQualifier()
    {
        _validator.Validate("SELECT ml.generate_text(body) FROM t", "bigquery").Should().StartWith("The function ml.generate_text is not allowed");
        SqlCrossDatabaseRule.Validate("SELECT SNOWFLAKE.CORTEX.COMPLETE('m', body) FROM t", "Snowflake", "SNOWFLAKE")
            .Should().StartWith("CORTEX.COMPLETE is not allowed");
    }

    // A family is matched on the qualifier of a call only: a table in a dataset or schema of that name is an ordinary
    // table, and a function of the same last name elsewhere is an ordinary function.
    [TestCase("bigquery", "SELECT SAFE_DIVIDE(a, b), FORMAT_DATE('%Y', d), APPROX_COUNT_DISTINCT(x), SESSION_USER() FROM t")]
    [TestCase("bigquery", "SELECT ST_DISTANCE(g1, g2), REGEXP_EXTRACT(s, 'a'), TO_JSON_STRING(s), FARM_FINGERPRINT(s) FROM t")]
    [TestCase("bigquery", "SELECT x FROM UNNEST(GENERATE_ARRAY(1, 3)) AS x")]
    [TestCase("bigquery", "SELECT p.score FROM ml.predictions p JOIN ai.labels l ON l.id = p.id")]
    [TestCase("bigquery", "SELECT ai_score, ml_flag, obj_count FROM t")]
    [TestCase("bigquery", "SELECT d.generate(1), d.predict(2) FROM t")]
    [TestCase("databricks", "SELECT from_json(j, 'a INT'), session_user(), current_user(), date_trunc('DAY', ts), uuid() FROM t")]
    [TestCase("databricks", "SELECT * FROM range(10)")]
    [TestCase("databricks", "SELECT ai_score, secret_count FROM t")]
    [TestCase("databricks", "SELECT parse_url(u, 'HOST'), url_decode(u), sha2(s, 256), h3_longlatash3(lon, lat, 7) FROM t")]
    [TestCase("Snowflake", "SELECT TRANSLATE(name, 'a', 'b'), CURRENT_USER(), CURRENT_ROLE(), CURRENT_WAREHOUSE(), PARSE_JSON(j) FROM t")]
    [TestCase("Snowflake", "SELECT DATEADD(day, 1, d), SPLIT_PART(s, ',', 1), ARRAY_SIZE(a), HASH(s), COMPLETE_FLAG FROM t")]
    [TestCase("Snowflake", "SELECT * FROM TABLE(FLATTEN(input => PARSE_JSON('[1]')))")]
    [TestCase("Snowflake", "SELECT * FROM TABLE(GENERATOR(ROWCOUNT => 3))")]
    [TestCase("Snowflake", "SELECT e.id FROM cortex.events e")]
    [TestCase("Snowflake", "SELECT ai_score, directory_name FROM t")]
    [TestCase("MSSQL", "SELECT * FROM OPENJSON(@j)")]
    [TestCase("MSSQL", "SELECT JSON_VALUE(j, '$.a'), STRING_AGG(x, ','), SUSER_SNAME() FROM t GROUP BY j")]
    [TestCase("AzureSynapse", "SELECT TOP 10 x, APPROX_COUNT_DISTINCT(y) FROM dbo.t GROUP BY x")]
    public void OrdinaryFunctions_Pass(string dialect, string sql)
    {
        _validator.Validate(sql, dialect).Should().BeNull();
        SqlCrossDatabaseRule.Validate(sql, dialect, ConfiguredDatabase).Should().BeNull();
    }

    [Test]
    public void Gate_WithReadOnlyEnforcementOff_StillRunsAProcedureByName()
    {
        SqlCrossDatabaseRule.Validate("EXEC dbo.usp_report 1", "MSSQL", ConfiguredDatabase).Should().BeNull();
    }

    private static IEnumerable<TestCaseData> RefusedCallStatements()
    {
        foreach (var (dialect, call) in RefusedCalls)
        {
            foreach (var placement in CallPlacements)
            {
                var sql = placement.Replace("{call}", call);
                yield return new TestCaseData(dialect, sql).SetArgDisplayNames(dialect, sql);
            }
        }
    }

    private static IEnumerable<TestCaseData> RefusedTableStatements()
    {
        foreach (var (dialect, table) in RefusedTables)
        {
            foreach (var placement in TablePlacements)
            {
                var sql = placement.Replace("{table}", table);
                yield return new TestCaseData(dialect, sql).SetArgDisplayNames(dialect, sql);
            }
        }
    }

    private static IEnumerable<TestCaseData> EveryRefusedStatement()
    {
        var procedures = RefusedProcedures.Select(x => new TestCaseData(x.Dialect, x.Sql).SetArgDisplayNames(x.Dialect, x.Sql));

        return RefusedCallStatements()
            .Concat(RefusedTableStatements())
            .Concat(procedures);
    }
}
