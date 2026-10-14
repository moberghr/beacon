using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;
using SqlParser.Dialects;

namespace Beacon.Tests.Unit;

/// <summary>
/// Every read-only check parses SQL in the data source's own dialect. BigQuery and Databricks carry no engine type, so
/// the dialect comes from the data source type; a source whose dialect is unknown is refused instead of being parsed
/// with a generic grammar that lexes strings and comments like no real engine.
/// </summary>
[TestFixture]
public class SqlReadOnlyValidatorDialectTests
{
    // A backslash-escaped quote: one SELECT to a generic grammar, three statements (one a CREATE) to BigQuery.
    private const string BigQueryEscapeDifferential =
        "SELECT 'x\\', ';/**/CREATE/**/OR/**/REPLACE/**/TABLE ds.t AS SELECT 1; SELECT 1 --'";

    [TestCase(DataSourceType.Database, DatabaseEngineType.PostgreSQL, "PostgreSQL")]
    [TestCase(DataSourceType.Database, DatabaseEngineType.AzureSynapse, "AzureSynapse")]
    [TestCase(DataSourceType.Database, DatabaseEngineType.SQLite, "SQLite")]
    [TestCase(DataSourceType.BigQuery, null, "bigquery")]
    [TestCase(DataSourceType.Databricks, null, "databricks")]
    [TestCase(DataSourceType.Database, null, null)]
    [TestCase(DataSourceType.CloudWatch, null, null)]
    [TestCase(DataSourceType.Api, null, null)]
    public void Of_MapsTypeAndEngineToTheDialect(DataSourceType type, DatabaseEngineType? engine, string? expected)
    {
        var dataSource = new DataSource
        {
            Name = "ds",
            DataSourceType = type,
            DatabaseEngineType = engine,
            EncryptedConnectionData = "unused"
        };

        DataSourceSqlDialect.Of(dataSource).Should().Be(expected);
    }

    [Test]
    public void Of_EveryDatabaseEngine_MapsToAnEngineGrammar()
    {
        foreach (var engine in Enum.GetValues<DatabaseEngineType>())
        {
            var dialect = DataSourceSqlDialect.Of(DataSourceType.Database, engine);

            SqlDialects.Resolve(dialect).Should().NotBeOfType<GenericDialect>(engine.ToString());
        }
    }

    [Test]
    public void Of_OnlySqlBearingSourceTypes_HaveADialectWithoutAnEngine()
    {
        var withDialect = Enum.GetValues<DataSourceType>()
            .Where(x => DataSourceSqlDialect.Of(x, null) != null)
            .ToList();

        withDialect.Should().BeEquivalentTo([DataSourceType.BigQuery, DataSourceType.Databricks]);
    }

    [TestCase("PostgreSQL", true)]
    [TestCase("postgres", true)]
    [TestCase("MSSQL", true)]
    [TestCase("sqlserver", true)]
    [TestCase("AzureSynapse", true)]
    [TestCase("MySQL", false)]
    [TestCase("MariaDB", false)]
    [TestCase("SQLite", false)]
    [TestCase("Snowflake", false)]
    [TestCase("bigquery", false)]
    [TestCase("databricks", false)]
    [TestCase(null, false)]
    public void NestsBlockComments_MatchesTheEngine(string? dialect, bool nests)
    {
        SqlDialects.NestsBlockComments(dialect).Should().Be(nests);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("SomethingElse")]
    [TestCase("CloudWatch")]
    public void Validate_UnknownDialect_RejectsEvenAPlainSelect(string? dialect)
    {
        var validator = new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance);

        validator.Validate("SELECT 1", dialect).Should().StartWith("The SQL dialect of this data source is not known");
    }

    [Test]
    public void Gate_BigQuerySource_ParsesInBigQueryDialectAndBlocksTheExtraStatements()
    {
        var dataSource = new DataSource
        {
            Name = "warehouse",
            DataSourceType = DataSourceType.BigQuery,
            EncryptedConnectionData = "unused"
        };

        var report = TestSqlGate.Create().Evaluate(
            SqlGateRequest.FromSettings(BigQueryEscapeDifferential, DataSourceSqlDialect.Of(dataSource), TestSqlGate.DefaultSettings()));

        report.Blocked.Should().BeTrue();
        report.Verdicts.ReadOnly.Code.Should().Be(SqlGateCodes.Ast);
    }

    [Test]
    public void Gate_SourceWithoutAKnownDialect_IsBlocked()
    {
        var report = TestSqlGate.Create().Evaluate(SqlGateRequest.FromSettings("SELECT 1", null, TestSqlGate.DefaultSettings()));

        report.Blocked.Should().BeTrue();
        report.BlockReason.Should().StartWith("The SQL dialect of this data source is not known");
    }
}
