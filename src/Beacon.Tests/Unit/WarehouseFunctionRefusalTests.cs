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
/// procedures that call out are refused by the boundary stage too, and so are the PostgreSQL, MySQL and SQL Server
/// functions that call HTTP endpoints, cloud services or models. Ordinary analytical functions and columns or literals of
/// the same names still pass.
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
        ("bigquery", "ML.PREDICT(m, body)"),
        ("bigquery", "ml.predict(m, body)"),
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
        ("Snowflake", "SYSTEM$SEND_EMAIL('integration', 'a@example.com', 's', body)"),
        ("PostgreSQL", "aws_s3.query_export_to_s3(q, uri)"),
        ("PostgreSQL", "query_export_to_s3(q, uri)"),
        ("PostgreSQL", "AWS_S3.Table_Import_From_S3('t', '', '(format csv)', uri)"),
        ("PostgreSQL", "aws_lambda.invoke('fn', body)"),
        ("PostgreSQL", "aws_ml.invoke_sagemaker_endpoint('endpoint', 1, body)"),
        ("PostgreSQL", "aws_comprehend.detect_sentiment(body, 'en')"),
        ("PostgreSQL", "aws_bedrock.invoke_model('model', 'application/json', 'application/json', body)"),
        ("PostgreSQL", "azure_ai.get_setting('azure_openai.endpoint')"),
        ("PostgreSQL", "azure_openai.create_embeddings('deployment', body)"),
        ("PostgreSQL", "\"azure_cognitive\".analyze_sentiment(body, 'en')"),
        ("PostgreSQL", "azure_ml.invoke(body)"),
        ("PostgreSQL", "azure_storage.blob_get('account', 'container', 'path')"),
        ("PostgreSQL", "ai.openai_embed('model', body)"),
        ("PostgreSQL", "google_ml.predict_row('model', body)"),
        ("PostgreSQL", "http(req)"),
        ("PostgreSQL", "http_get('https://example.com')"),
        ("PostgreSQL", "extensions.HTTP_POST('https://example.com', body, 'text/plain')"),
        ("PostgreSQL", "net.http_get('https://example.com')"),
        ("PostgreSQL", "http_put(u, body, 'text/plain')"),
        ("PostgreSQL", "http_patch(u, body, 'text/plain')"),
        ("PostgreSQL", "http_delete(u)"),
        ("PostgreSQL", "\"http_head\"(u)"),
        ("MySQL", "lambda_sync('arn', body)"),
        ("MySQL", "LAMBDA_ASYNC('arn', body)"),
        ("MySQL", "mysql.lambda_async('arn', body)"),
        ("MySQL", "sys.ML_PREDICT_ROW(body, 'model', NULL)"),
        ("MySQL", "sys.ml_explain_row(body, 'model', NULL)"),
        ("MySQL", "ML_GENERATE(body, NULL)"),
        ("MySQL", "ml_embed_row(body, NULL)"),
        ("MSSQL", "AI_GENERATE_EMBEDDINGS(body)"),
        ("AzureSynapse", "ai_generate_embeddings(body)")
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
        ("databricks", "'orders'"),
        ("PostgreSQL", "aws_s3.table_import_from_s3('t', '', '(format csv)', uri)"),
        ("PostgreSQL", "http_get('https://example.com')"),
        ("MySQL", "sys.ML_PREDICT_TABLE('db.t', 'model', 'db.out', NULL)"),
        ("MSSQL", "AI_GENERATE_CHUNKS(body)")
    ];

    // Procedures that call out: refused by the boundary stage whether or not read-only enforcement is on.
    private static readonly (string Dialect, string Sql)[] RefusedProcedures =
    [
        ("MSSQL", "EXEC sp_invoke_external_rest_endpoint @url = 'https://example.com'"),
        ("MSSQL", "EXEC dbo.sp_send_dbmail @recipients = 'a@example.com'"),
        ("MSSQL", "EXEC sp_execute_external_script @language = N'Python', @script = N'x'"),
        ("MSSQL", "EXEC sp_OAMethod @o, 'Run'"),
        ("AzureSynapse", "EXEC [sp_invoke_external_rest_endpoint] @url = 'https://example.com'"),
        ("MySQL", "CALL mysql.lambda_async('arn', '{}')"),
        ("MySQL", "CALL sys.ML_PREDICT_TABLE('db.t', 'model', 'db.out', NULL)")
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
    [TestCase("PostgreSQL", "SELECT http, http_get, t.http_post, t.query_export_to_s3 FROM t")]
    [TestCase("PostgreSQL", "SELECT 'http_get(u)' AS http, 'aws_s3.query_export_to_s3(q)' AS v FROM t")]
    [TestCase("PostgreSQL", "SELECT public.http_status(code), app.ai_score(x), my_ai.score(x) FROM t")]
    [TestCase("PostgreSQL", "SELECT v.id FROM ai.vectorizer v JOIN aws_s3.exports e ON e.id = v.id")]
    [TestCase("MySQL", "SELECT lambda_sync, ml_generate, sys_flag FROM t")]
    [TestCase("MySQL", "SELECT 'lambda_async(1)' AS lambda_async, 'sys.ML_GENERATE' AS ml_embed_row FROM t")]
    [TestCase("MySQL", "SELECT home.ml_score(x) FROM t")]
    [TestCase("MSSQL", "SELECT ai_generate_embeddings, 'AI_GENERATE_CHUNKS(x)' AS AI_GENERATE_CHUNKS FROM t")]
    public void OrdinaryFunctions_Pass(string dialect, string sql)
    {
        _validator.Validate(sql, dialect).Should().BeNull();
        SqlCrossDatabaseRule.Validate(sql, dialect, ConfiguredDatabase).Should().BeNull();
    }

    // Only a two-part name can be a BigQuery family member; with a project part it is a user function in a dataset.
    [TestCase("SELECT proj.ml.normalize(x) FROM t")]
    [TestCase("SELECT proj.AI.score(x), proj.obj.fetch(x) FROM t")]
    [TestCase("SELECT `proj.ml.normalize`(x) FROM t")]
    [TestCase("SELECT * FROM proj.ml.predictions_for((SELECT 1))")]
    public void BigQuery_UserFunctionInADatasetNamedLikeAFamily_Passes(string sql)
    {
        _validator.Validate(sql, "bigquery").Should().BeNull();
        SqlCrossDatabaseRule.Validate(sql, "bigquery", "proj").Should().BeNull();
    }

    [TestCase("SELECT AI_GENERATE_EMBEDDINGS(body USE MODEL m) AS v FROM t")]
    [TestCase("SELECT c.chunk FROM t CROSS APPLY AI_GENERATE_CHUNKS(source = t.body, chunk_type = FIXED, chunk_size = 50) c")]
    public void TSql_EmbeddingFunctionsInTheirOwnSyntax_AreRefused(string sql)
    {
        SqlCrossDatabaseRule.Validate(sql, "MSSQL", ConfiguredDatabase).Should().NotBeNull();
        _validator.Validate(sql, "MSSQL").Should().NotBeNull();
    }

    [Test]
    public void Validator_NamesTheSchemaOfAnExtensionFunctionRefusedByIt()
    {
        SqlCrossDatabaseRule.Validate("SELECT aws_s3.query_export_to_s3(q, uri) FROM t", "PostgreSQL", ConfiguredDatabase)
            .Should().StartWith("aws_s3.query_export_to_s3 is not allowed");
        _validator.Validate("SELECT sys.ML_PREDICT_ROW(body, 'm', NULL) FROM t", "MySQL")
            .Should().StartWith("The function sys.ML_PREDICT_ROW is not allowed");
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
