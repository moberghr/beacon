using System.Collections.Concurrent;
using System.Data.Common;
using System.Reflection;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.HostData;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// A statement names objects of its data source's own database only: per dialect, a name qualified with the database
/// (catalog, project) the source's connection is configured for runs, one qualified with any other is refused — by the
/// rule itself, by the shared gate (with read-only enforcement on or off), when a query or step is saved, and when a
/// saved step runs (previews and subscription runs). Unqualified and schema-qualified names always pass; a source with no
/// configured database accepts no qualifier. No database is reached: the step-execution case stops at the connection.
/// </summary>
[TestFixture]
[NonParallelizable] // DbConnectionFactory is a process-wide registry.
public class CrossDatabaseQualifierTests
{
    private const int DataSourceId = 5;

    private Func<string, DbConnection>? _previousPostgreSqlFactory;

    public static IEnumerable<TestCaseData> Dialects()
    {
        return Cases().Select(x => new TestCaseData(x).SetArgDisplayNames(x.Name));
    }

    [SetUp]
    public void SetUp()
    {
        Registry().TryGetValue(DatabaseEngineType.PostgreSQL, out _previousPostgreSqlFactory);
    }

    [TearDown]
    public void TearDown()
    {
        if (_previousPostgreSqlFactory == null)
        {
            Registry().TryRemove(DatabaseEngineType.PostgreSQL, out _);
            return;
        }

        Registry()[DatabaseEngineType.PostgreSQL] = _previousPostgreSqlFactory;
    }

    [TestCaseSource(nameof(Dialects))]
    public void Rule_AcceptsTheConfiguredDatabase_AndRefusesAnother(QualifierCase dialect)
    {
        SqlCrossDatabaseRule.Validate(dialect.Matching, dialect.Dialect, dialect.Configured).Should().BeNull();
        SqlCrossDatabaseRule.Validate(dialect.Foreign, dialect.Dialect, dialect.Configured).Should().Contain($"another {dialect.Noun}");
        SqlCrossDatabaseRule.Validate(dialect.ForeignFunction, dialect.Dialect, dialect.Configured).Should().Contain($"another {dialect.Noun}");
    }

    [TestCaseSource(nameof(Dialects))]
    public void Rule_AcceptsUnqualifiedAndSchemaQualifiedNames_WithoutAConfiguredDatabase(QualifierCase dialect)
    {
        SqlCrossDatabaseRule.Validate(dialect.Local, dialect.Dialect, null).Should().BeNull();
    }

    [TestCaseSource(nameof(Dialects))]
    public void Rule_WithoutAConfiguredDatabase_RefusesEveryQualifier(QualifierCase dialect)
    {
        SqlCrossDatabaseRule.Validate(dialect.Matching, dialect.Dialect, null).Should().Contain($"no {dialect.Noun} configured");
    }

    [TestCaseSource(nameof(Dialects))]
    public void Rule_RefusesNamesWithMorePartsThanTheFullForm(QualifierCase dialect)
    {
        SqlCrossDatabaseRule.Validate(dialect.TooManyParts, dialect.Dialect, dialect.Configured).Should().Contain("more parts than");
    }

    [TestCaseSource(nameof(Dialects))]
    public void ConfiguredDatabase_IsReadFromTheConnectionData(QualifierCase dialect)
    {
        DataSourceConfiguredDatabase.Of(dialect.Type, dialect.Engine, dialect.ConnectionData).Should().Be(dialect.Configured);
    }

    [TestCaseSource(nameof(Dialects))]
    public void Gate_RunsTheConfiguredDatabase_AndBlocksAnother_WithReadOnlyEnforcementOnOrOff(QualifierCase dialect)
    {
        var gate = TestSqlGate.Create();

        foreach (var enforceReadOnly in new[] { true, false })
        {
            var matching = gate.Evaluate(Request(dialect.Matching, dialect, enforceReadOnly));
            var foreign = gate.Evaluate(Request(dialect.Foreign, dialect, enforceReadOnly));

            matching.Blocked.Should().BeFalse($"{dialect.Matching} names the configured {dialect.Noun}");
            foreign.Blocked.Should().BeTrue($"{dialect.Foreign} names another {dialect.Noun}");
            foreign.Verdicts.Schema.Code.Should().Be(SqlGateCodes.CrossDatabase);
            foreign.BlockReason.Should().Contain($"another {dialect.Noun}");
            foreign.Verdicts.RowLimit.Code.Should().Be(SqlGateCodes.NotEvaluated);
        }
    }

    // What reaches beyond the data source — a cross-connection or file function or procedure, a Databricks path, a T-SQL
    // linked-server name — is refused whether or not read-only enforcement is on.
    [TestCase("PostgreSQL", "SELECT * FROM dblink('host=x', 'select 1')")]
    [TestCase("PostgreSQL", "SELECT lo_import('/etc/passwd')")]
    [TestCase("PostgreSQL", "SELECT public.dblink_connect('host=x')")]
    [TestCase("MSSQL", "SELECT * FROM OPENROWSET('SQLNCLI', 'Server=x;', 'SELECT 1') AS r")]
    [TestCase("MSSQL", "SELECT * FROM OPENQUERY(srv, 'SELECT 1')")]
    [TestCase("MSSQL", "SELECT * FROM OPENDATASOURCE('SQLNCLI', 'Data Source=x')")]
    [TestCase("MSSQL", "SELECT xp_dirtree('c:')")]
    [TestCase("MSSQL", "EXEC xp_cmdshell 'dir'")]
    [TestCase("MSSQL", "SELECT * FROM srv.Sales.dbo.orders")]
    [TestCase("AzureSynapse", "SELECT * FROM [srv].[Sales].[dbo].[orders]")]
    [TestCase("MySQL", "SELECT * FROM OPENQUERY(srv, 'SELECT 1')")]
    [TestCase("bigquery", "SELECT * FROM EXTERNAL_QUERY('conn', 'SELECT 1')")]
    [TestCase("databricks", "SELECT * FROM read_files('/mnt/x')")]
    [TestCase("databricks", "SELECT * FROM json.`/mnt/landing/x`")]
    [TestCase("databricks", "SELECT * FROM `s3://bucket/x`")]
    [TestCase("Snowflake", "SELECT * FROM TABLE(EXTERNAL_QUERY('conn', 'SELECT 1'))")]
    public void Gate_WithReadOnlyEnforcementOff_StillRefusesWhatReachesBeyondTheSource(string dialect, string sql)
    {
        var request = SqlGateRequest.FromSettings(sql, dialect, TestSqlGate.DefaultSettings()) with
        {
            EnforceReadOnly = false,
            ConfiguredDatabase = "Sales"
        };

        var report = TestSqlGate.Create().Evaluate(request);

        report.Blocked.Should().BeTrue();
        report.Verdicts.ReadOnly.Status.Should().NotBe(SqlGateStatus.Fail, "read-only enforcement is off; the boundary stage refuses it");
        report.Verdicts.Schema.Code.Should().Be(SqlGateCodes.CrossDatabase);
        SqlCrossDatabaseRule.Validate(sql, dialect, "Sales").Should().NotBeNull();
    }

    [Test]
    public void Gate_WithReadOnlyEnforcementOff_StillRunsWhatStaysInTheSource()
    {
        var request = SqlGateRequest.FromSettings("SELECT lower(name) FROM public.orders", "PostgreSQL", TestSqlGate.DefaultSettings()) with
        {
            EnforceReadOnly = false,
            ConfiguredDatabase = "sales"
        };

        TestSqlGate.Create().Evaluate(request).Blocked.Should().BeFalse();
    }

    // A statement that changes the database, schema, role or user it runs in, or runs SQL held in a string, a variable or
    // the session, is refused whether or not read-only enforcement is on — when it runs and when it is saved.
    [TestCase("MSSQL", "USE HR; SELECT * FROM dbo.Customers", "changes the database")]
    [TestCase("MSSQL", "USE [HR]", "changes the database")]
    [TestCase("AzureSynapse", "USE HR", "changes the database")]
    [TestCase("MSSQL", "EXEC @sql", "runs SQL held in")]
    [TestCase("MSSQL", "EXEC sp_executesql N'SELECT * FROM HR.dbo.t'", "runs SQL held in")]
    [TestCase("MSSQL", "EXECUTE sys.sp_executesql @stmt = N'SELECT 1'", "runs SQL held in")]
    [TestCase("MSSQL", "EXEC [sp_executesql] @sql", "runs SQL held in")]
    [TestCase("MSSQL", "EXEC sp_sqlexec 'SELECT 1'", "runs SQL held in")]
    [TestCase("MSSQL", "EXEC sp_MSforeachdb 'SELECT 1'", "runs SQL held in")]
    [TestCase("MSSQL", "SELECT 1; EXEC sp_executesql @sql", "runs SQL held in")]
    [TestCase("MSSQL", "EXECUTE AS USER = 'reporting_admin'", "changes the role or user")]
    [TestCase("MSSQL", "EXECUTE AS LOGIN = 'sa'", "changes the role or user")]
    [TestCase("MSSQL", "EXEC sp_setapprole 'auditor', 'secret'", "changes the role or user")]
    [TestCase("MSSQL", "SET ROLE admin", "changes the role or user")]
    [TestCase("MySQL", "USE hr", "changes the database")]
    [TestCase("MySQL", "USE `hr`; SELECT * FROM salaries", "changes the database")]
    [TestCase("MySQL", "EXECUTE stmt", "runs SQL held in")]
    [TestCase("MySQL", "EXECUTE stmt USING @a", "runs SQL held in")]
    [TestCase("MySQL", "EXECUTE IMMEDIATE 'SELECT 1'", "runs SQL held in")]
    [TestCase("MySQL", "SET ROLE ALL", "changes the role or user")]
    [TestCase("PostgreSQL", "SET ROLE admin", "changes the role or user")]
    [TestCase("PostgreSQL", "SET SESSION ROLE admin", "changes the role or user")]
    [TestCase("PostgreSQL", "SET LOCAL ROLE admin; SELECT * FROM public.salaries", "changes the role or user")]
    [TestCase("PostgreSQL", "SET session_authorization = 'admin'", "changes the role or user")]
    [TestCase("PostgreSQL", "SET \"role\" = 'admin'", "changes the role or user")]
    [TestCase("PostgreSQL", "SELECT set_config('role', 'admin', false)", "change the role")]
    [TestCase("PostgreSQL", "PREPARE p AS SELECT 1", "runs SQL held in")]
    [TestCase("PostgreSQL", "EXECUTE p(1)", "runs SQL held in")]
    [TestCase("PostgreSQL", "SELECT query_to_xml('SELECT * FROM dblink(''host=x'', ''select 1'') AS t(a int)', true, false, '')", "runs SQL held in")]
    [TestCase("PostgreSQL", "SELECT * FROM crosstab('SELECT 1, 2, 3')", "runs SQL held in")]
    [TestCase("PostgreSQL", "SELECT * FROM ts_stat('SELECT v FROM public.docs')", "runs SQL held in")]
    [TestCase("PostgreSQL", "SELECT * FROM connectby('public.tree', 'id', 'parent_id', '1', 0)", "names its object in a string")]
    [TestCase("PostgreSQL", "CREATE FUNCTION f() RETURNS int AS $$ SELECT 1 $$ LANGUAGE sql", "defined from a string")]
    [TestCase("PostgreSQL", "CREATE FUNCTION f() RETURNS int LANGUAGE sql AS 'SELECT 1'", "defined from a string")]
    [TestCase("Snowflake", "USE HR", "changes the database")]
    [TestCase("Snowflake", "USE DATABASE HR", "changes the database")]
    [TestCase("Snowflake", "USE SCHEMA HR.PUBLIC", "changes the database")]
    [TestCase("Snowflake", "USE WAREHOUSE BIG_WH", "changes the database")]
    [TestCase("Snowflake", "EXECUTE IMMEDIATE 'SELECT 1'", "runs SQL held in")]
    [TestCase("Snowflake", "EXECUTE IMMEDIATE $$SELECT 1$$", "runs SQL held in")]
    [TestCase("Snowflake", "EXECUTE IMMEDIATE :q", "runs SQL held in")]
    [TestCase("Snowflake", "EXECUTE TASK refresh_task", "runs SQL held in")]
    [TestCase("databricks", "USE CATALOG hive_metastore", "changes the catalog")]
    [TestCase("databricks", "USE SCHEMA hive_metastore.sales", "changes the catalog")]
    [TestCase("databricks", "USE DATABASE sales", "changes the catalog")]
    [TestCase("databricks", "USE hive_metastore", "changes the catalog")]
    [TestCase("databricks", "SET spark.sql.defaultCatalog = hive_metastore", "changes the catalog")]
    [TestCase("databricks", "SET `spark.sql.catalog.remote` = x", "changes the catalog")]
    [TestCase("databricks", "EXECUTE IMMEDIATE 'SELECT 1'", "runs SQL held in")]
    [TestCase("databricks", "EXECUTE IMMEDIATE sqlStr", "runs SQL held in")]
    [TestCase("bigquery", "EXECUTE IMMEDIATE 'SELECT * FROM `acme-dev.sales.t`'", "runs SQL held in")]
    [TestCase("bigquery", "EXECUTE IMMEDIATE \"SELECT 1\"", "runs SQL held in")]
    [TestCase("bigquery", "USE other", "changes the project")]
    [TestCase("SQLite", "ATTACH DATABASE '/data/other.db' AS o", "opens another database")]
    [TestCase("SQLite", "ATTACH '/data/other.db' AS o", "opens another database")]
    public void Gate_WithReadOnlyEnforcementOff_RefusesContextSwitchesAndDynamicSql(string dialect, string sql, string reason)
    {
        var request = SqlGateRequest.FromSettings(sql, dialect, TestSqlGate.DefaultSettings()) with
        {
            EnforceReadOnly = false,
            ConfiguredDatabase = "Sales",
            MaxRows = 10
        };

        var report = TestSqlGate.Create().Evaluate(request);

        report.Blocked.Should().BeTrue();
        report.Verdicts.ReadOnly.Status.Should().NotBe(SqlGateStatus.Fail, "read-only enforcement is off; the boundary stage refuses it");
        report.Verdicts.Schema.Code.Should().Be(SqlGateCodes.CrossDatabase);
        report.BlockReason.Should().Contain(reason);
        SqlCrossDatabaseRule.ValidateIfParsed(sql, dialect, "Sales").Should().Contain(reason, "a query or step holding it is not saved either");
    }

    // Forms the parser cannot represent are refused as SQL whose names cannot be checked.
    [TestCase("MSSQL", "EXEC('SELECT * FROM HR.dbo.t')")]
    [TestCase("MSSQL", "EXEC (@sql)")]
    [TestCase("MSSQL", "DECLARE @sql NVARCHAR(100) = N'USE HR'; EXEC(@sql)")]
    [TestCase("MSSQL", "INSERT INTO dbo.t (a) EXEC('SELECT 1')")]
    [TestCase("MSSQL", "SETUSER 'admin'")]
    [TestCase("PostgreSQL", "DO $$ BEGIN EXECUTE 'SELECT 1'; END $$")]
    [TestCase("PostgreSQL", "SET SESSION AUTHORIZATION admin")]
    [TestCase("PostgreSQL", "SET role TO admin")]
    [TestCase("MySQL", "PREPARE stmt FROM 'SELECT * FROM hr.salaries'")]
    [TestCase("MySQL", "PREPARE stmt FROM @s")]
    [TestCase("MySQL", "SET DEFAULT ROLE admin TO reader")]
    [TestCase("Snowflake", "USE ROLE ACCOUNTADMIN")]
    [TestCase("Snowflake", "USE SECONDARY ROLES ALL")]
    [TestCase("Snowflake", "ALTER SESSION SET QUERY_TAG = 'x'")]
    [TestCase("databricks", "SET CATALOG hive_metastore")]
    [TestCase("databricks", "SET SCHEMA sales")]
    [TestCase("bigquery", "SET @@dataset_project_id = 'acme-dev'")]
    [TestCase("bigquery", "EXECUTE IMMEDIATE q USING 1 AS x")]
    public void Gate_WithReadOnlyEnforcementOff_RefusesSwitchesAndDynamicSqlTheParserCannotRead(string dialect, string sql)
    {
        var request = SqlGateRequest.FromSettings(sql, dialect, TestSqlGate.DefaultSettings()) with
        {
            EnforceReadOnly = false,
            ConfiguredDatabase = "Sales",
            MaxRows = 10
        };

        var report = TestSqlGate.Create().Evaluate(request);

        report.Blocked.Should().BeTrue();
        report.Verdicts.Schema.Code.Should().Be(SqlGateCodes.CrossDatabase);
        report.BlockReason.Should().Contain("could not be parsed");
    }

    // With read-only enforcement off, writes, batches and settings that stay in the configured database still run, and so
    // does a T-SQL EXEC of a procedure by name.
    [TestCase("MSSQL", "Sales", "INSERT INTO dbo.t (a) VALUES (1); UPDATE Sales.dbo.t SET a = 2 WHERE a = 1; DELETE FROM t WHERE a = 3")]
    [TestCase("MSSQL", "Sales", "EXEC dbo.refresh_totals 1, 2")]
    [TestCase("AzureSynapse", "Sales", "UPDATE [Sales].[dbo].[t] SET a = 2; SELECT a FROM dbo.t")]
    [TestCase("PostgreSQL", "sales", "SET search_path TO public; INSERT INTO t (a) VALUES (1); UPDATE sales.public.t SET a = 2")]
    [TestCase("PostgreSQL", "sales", "SET statement_timeout = 1000; CALL public.refresh_totals()")]
    [TestCase("MySQL", "sales", "SET @x = 1; INSERT INTO sales.t (a) VALUES (@x); UPDATE t SET a = 2")]
    [TestCase("Snowflake", "SALES", "SET v = 'x'; INSERT INTO public.t (a) VALUES (1); UPDATE SALES.PUBLIC.T SET a = 2")]
    [TestCase("databricks", "main", "SET ansi_mode = true; INSERT INTO main.sales.t (a) VALUES (1); UPDATE sales.t SET a = 2")]
    [TestCase("bigquery", "acme-prod", "INSERT INTO sales.t (a) VALUES (1); UPDATE `acme-prod.sales.t` SET a = 2 WHERE true")]
    [TestCase("SQLite", DataSourceConfiguredDatabase.SqliteMain, "INSERT INTO main.t (a) VALUES (1); UPDATE t SET a = 2")]
    public void Gate_WithReadOnlyEnforcementOff_StillRunsAWriteBatchInTheConfiguredDatabase(string dialect, string configured, string sql)
    {
        var request = SqlGateRequest.FromSettings(sql, dialect, TestSqlGate.DefaultSettings()) with
        {
            EnforceReadOnly = false,
            ConfiguredDatabase = configured
        };

        var report = TestSqlGate.Create().Evaluate(request);

        report.Blocked.Should().BeFalse(report.BlockReason);
        report.Verdicts.Schema.Code.Should().NotBe(SqlGateCodes.CrossDatabase);
        SqlCrossDatabaseRule.Validate(sql, dialect, configured).Should().BeNull();
    }

    // The T-SQL full-text and semantic rowset functions name the table they read as their first argument.
    [TestCase("SELECT * FROM CONTAINSTABLE(HR.dbo.docs, body, 'x') AS k", false)]
    [TestCase("SELECT * FROM dbo.a CROSS APPLY FREETEXTTABLE(HR.dbo.docs, *, 'x') AS k", false)]
    [TestCase("SELECT * FROM SEMANTICKEYPHRASETABLE(HR.dbo.docs, body) AS k", false)]
    [TestCase("SELECT * FROM SEMANTICSIMILARITYTABLE(HR.dbo.docs, body, 1) AS k", false)]
    [TestCase("SELECT * FROM SEMANTICSIMILARITYDETAILSTABLE(HR.dbo.docs, body, 1, body, 2) AS k", false)]
    [TestCase("SELECT * FROM CONTAINSTABLE(srv.Sales.dbo.docs, body, 'x') AS k", false)]
    [TestCase("SELECT * FROM CONTAINSTABLE('HR.dbo.docs', body, 'x') AS k", false)]
    [TestCase("SELECT * FROM CONTAINSTABLE(Sales.dbo.docs, body, 'x') AS k", true)]
    [TestCase("SELECT * FROM CONTAINSTABLE(dbo.docs, body, 'x') AS k", true)]
    [TestCase("SELECT * FROM FREETEXTTABLE(docs, *, 'x') AS k", true)]
    public void Rule_ChecksTheTableAFullTextFunctionReads(string sql, bool allowed)
    {
        var rejection = SqlCrossDatabaseRule.Validate(sql, "MSSQL", "Sales");

        if (allowed)
        {
            rejection.Should().BeNull();
            return;
        }

        rejection.Should().NotBeNull();
    }

    // GET_DDL and OBJECT_DEFINITION read an object named in a string, which no name rule can see; OBJECT_ID returns a number.
    [TestCase("Snowflake", "SELECT GET_DDL('table', 'HR.PUBLIC.SALARIES')", false)]
    [TestCase("Snowflake", "SELECT get_ddl('view', 'v')", false)]
    [TestCase("MSSQL", "SELECT OBJECT_DEFINITION(OBJECT_ID('HR.dbo.p'))", false)]
    [TestCase("MSSQL", "SELECT OBJECT_ID('dbo.orders')", true)]
    public void Rule_RefusesFunctionsThatNameTheirObjectInAString(string dialect, string sql, bool allowed)
    {
        var rejection = SqlCrossDatabaseRule.Validate(sql, dialect, "Sales");

        if (allowed)
        {
            rejection.Should().BeNull();
            return;
        }

        rejection.Should().Contain("names its object in a string");
    }

    // The configured database must match the whole qualifier, not a prefix of it or the other way round.
    [TestCase("PostgreSQL", "sales", "SELECT 1 FROM sal.public.t")]
    [TestCase("PostgreSQL", "sales", "SELECT 1 FROM sales2.public.t")]
    [TestCase("MSSQL", "Sales", "SELECT 1 FROM Sal.dbo.t")]
    [TestCase("MSSQL", "Sales", "SELECT 1 FROM Sales2.dbo.t")]
    [TestCase("MySQL", "sales", "SELECT 1 FROM sal.t")]
    [TestCase("MySQL", "sales", "SELECT 1 FROM sales_archive.t")]
    [TestCase("Snowflake", "SALES", "SELECT 1 FROM SALE.PUBLIC.T")]
    [TestCase("databricks", "main", "SELECT 1 FROM mai.s.t")]
    [TestCase("bigquery", "acme-prod", "SELECT 1 FROM `acme-prod2.sales.t`")]
    public void Rule_RefusesANameThatOnlyStartsLikeTheConfiguredDatabase(string dialect, string configured, string sql)
    {
        SqlCrossDatabaseRule.Validate(sql, dialect, configured).Should().Contain("another");
    }

    // Each engine's system catalog sits in a database of its own, so qualifying it is naming another database.
    [TestCase("MySQL", "sales", "SELECT * FROM information_schema.tables")]
    [TestCase("MySQL", "sales", "SELECT * FROM mysql.user")]
    [TestCase("MySQL", "sales", "SELECT * FROM performance_schema.threads")]
    [TestCase("Snowflake", "SALES", "SELECT * FROM SNOWFLAKE.ACCOUNT_USAGE.QUERY_HISTORY")]
    [TestCase("databricks", "main", "SELECT * FROM system.access.audit")]
    [TestCase("MSSQL", "Sales", "SELECT * FROM master.sys.sql_logins")]
    public void Rule_RefusesTheSystemCatalogOfAnotherDatabase(string dialect, string configured, string sql)
    {
        SqlCrossDatabaseRule.Validate(sql, dialect, configured).Should().Contain("another");
    }

    // A connection that cannot be read is not the same as one that names no database: the refusal says which.
    [Test]
    public void Rule_WhenTheConfiguredDatabaseCouldNotBeDetermined_SaysSo_AndStillAllowsLocalNames()
    {
        var undetermined = SqlCrossDatabaseRule.Validate("SELECT 1 FROM sales.public.t", "PostgreSQL", DataSourceConfiguredDatabase.Undetermined);
        var absent = SqlCrossDatabaseRule.Validate("SELECT 1 FROM sales.public.t", "PostgreSQL", null);

        undetermined.Should().Contain("the data source's database could not be determined");
        absent.Should().Contain("no database configured");
        SqlCrossDatabaseRule.Validate("SELECT 1 FROM public.t", "PostgreSQL", DataSourceConfiguredDatabase.Undetermined).Should().BeNull();
        SqlCrossDatabaseRule.Validate("SELECT 1 FROM \"\0undetermined\".public.t", "PostgreSQL", DataSourceConfiguredDatabase.Undetermined).Should().NotBeNull();
    }

    [Test]
    public void Resolver_LogsAConnectionStringItCannotParse_ByDataSourceIdOnly()
    {
        var dataSource = new DataSource { Id = 14, Name = "warehouse", DataSourceType = DataSourceType.Database, DatabaseEngineType = DatabaseEngineType.MSSQL, EncryptedConnectionData = "enc" };
        var connections = new Mock<IDataSourceConnectionResolver>();
        connections.Setup(x => x.GetConnectionString(dataSource)).Returns("Server=db;Password=hunter2;Database='Sales");
        var logs = new LogRecorder();

        var configured = new ConfiguredDatabaseResolver(connections.Object, logs.For<ConfiguredDatabaseResolver>()).Resolve(dataSource);

        configured.Should().Be(DataSourceConfiguredDatabase.Undetermined);
        var entry = logs.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Contain("14").And.Contain("could not be parsed").And.NotContain("hunter2").And.NotContain("Sales");
    }

    [Test]
    public void Gate_WithoutAConfiguredDatabase_BlocksAQualifiedName()
    {
        var report = TestSqlGate.Create().Evaluate(SqlGateRequest.FromSettings("SELECT id FROM sales.public.orders", "PostgreSQL", TestSqlGate.DefaultSettings()));

        report.Blocked.Should().BeTrue();
        report.Verdicts.Schema.Code.Should().Be(SqlGateCodes.CrossDatabase);
    }

    // Read-only enforcement off lets SQL the parser cannot read past the read-only stage; its names cannot be checked.
    [TestCase("SELECT ??? FROM sales.public.orders")]
    [TestCase("SELECT ((((((((((((((((((((((((((((((((((((((((((((((((((((((((((1)))))))))))))))))))))))))))))))))))))))))))))))))))))))))) AS x")]
    public void Gate_WithReadOnlyEnforcementOff_BlocksSqlWhoseNamesCannotBeChecked(string sql)
    {
        var request = SqlGateRequest.FromSettings(sql, "PostgreSQL", TestSqlGate.DefaultSettings()) with
        {
            EnforceReadOnly = false,
            ConfiguredDatabase = "sales",
            MaxRows = 10
        };

        var report = TestSqlGate.Create().Evaluate(request);

        report.Blocked.Should().BeTrue();
        report.Verdicts.Schema.Code.Should().Be(SqlGateCodes.CrossDatabase);
    }

    [TestCaseSource(nameof(Dialects))]
    public async Task CreateQuery_RefusesAStepNamingAnotherDatabase_AndSavesTheConfiguredOne(QualifierCase dialect)
    {
        var saved = new List<object>();
        var service = Service(dialect, saved);

        var refused = () => service.CreateQuery(NewQuery(dialect.Foreign), CancellationToken.None);
        await refused.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*another {dialect.Noun}*");
        saved.Should().BeEmpty("nothing is stored when a step names another database");

        var response = await service.CreateQuery(NewQuery(dialect.Matching), CancellationToken.None);
        response.Success.Should().BeTrue();
        saved.OfType<Query>().Should().ContainSingle();
    }

    [TestCaseSource(nameof(Dialects))]
    public async Task AddQueryStep_RefusesSqlNamingAnotherDatabase_AndAcceptsTheConfiguredOne(QualifierCase dialect)
    {
        var service = Service(dialect, []);

        var refused = () => service.AddQueryStep(1, Step(dialect.Foreign), CancellationToken.None);
        var accepted = () => service.AddQueryStep(1, Step(dialect.Matching), CancellationToken.None);

        await refused.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*another {dialect.Noun}*");
        await accepted.Should().NotThrowAsync();
    }

    [Test]
    public async Task UpdateQuery_RefusesAnEditNamingAnotherDatabase_BeforeAnyVersionIsWritten()
    {
        var dialect = Cases().First(x => x.Name == "PostgreSQL");
        var versions = new Mock<IQueryVersionService>(MockBehavior.Strict);
        var stored = new Query
        {
            Id = 9,
            Name = "orders",
            Steps = [new QueryStep { Id = 91, QueryId = 9, DataSourceId = DataSourceId, StepOrder = 1, SqlValue = dialect.Matching }]
        };
        var service = Service(dialect, [], versions.Object, stored);
        var edit = new QueryData
        {
            QueryId = 9,
            Name = "orders",
            Steps = [new QueryStepData { StepId = 91, StepOrder = 1, Name = "step", SqlValue = dialect.Foreign, DataSourceId = DataSourceId }]
        };

        var act = () => service.UpdateQuery(edit, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*another database*");
        versions.VerifyNoOtherCalls();
    }

    // The check judges the SQL as it runs: a {placeholder} is bound first, so a qualifier next to one is still seen.
    [Test]
    public async Task CreateQuery_JudgesTheBoundSql()
    {
        var dialect = Cases().First(x => x.Name == "PostgreSQL");
        var service = Service(dialect, []);
        var query = NewQuery("SELECT id FROM hr.public.orders WHERE id = {id}");
        query.Steps[0].Parameters = [new QueryStepParameterData { Name = "id", Type = ParameterType.Number, Placeholder = "{id}" }];

        var act = () => service.CreateQuery(query, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*another database*");
    }

    // SQL the parser cannot read is saved as before: it never runs, because every execution gate refuses it.
    [Test]
    public async Task CreateQuery_SavesSqlTheParserCannotRead_AsBefore()
    {
        var dialect = Cases().First(x => x.Name == "PostgreSQL");
        var saved = new List<object>();

        var response = await Service(dialect, saved).CreateQuery(NewQuery("SELECT FROM WHERE"), CancellationToken.None);

        response.Success.Should().BeTrue();
        saved.OfType<Query>().Should().ContainSingle();
    }

    // Previews and subscription runs execute a saved step through the same path: the qualifier is checked on the bound SQL
    // right before the connection is opened.
    [TestCase("SELECT id FROM hr.public.orders", false)]
    [TestCase("SELECT id FROM sales.public.orders", true)]
    public async Task StepExecution_RefusesAnotherDatabase_BeforeConnecting(string sql, bool reachesTheConnection)
    {
        var dialect = Cases().First(x => x.Name == "PostgreSQL");
        Registry()[DatabaseEngineType.PostgreSQL] = _ => throw new ConnectionReachedException();
        var step = new QueryStep { Id = 51, QueryId = 1, DataSourceId = DataSourceId, StepOrder = 1, SqlValue = sql };
        var service = Service(dialect, [], steps: [step]);

        var act = () => service.PreviewQueryStep(1, 1, CancellationToken.None);

        if (reachesTheConnection)
        {
            await act.Should().ThrowAsync<ConnectionReachedException>();
            return;
        }

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*another database*");
    }

    [Test]
    public void Rule_FollowsTheEnginesCaseRules()
    {
        // PostgreSQL folds an unquoted name to lower case; a quoted one is exact.
        SqlCrossDatabaseRule.Validate("SELECT 1 FROM SALES.public.t", "PostgreSQL", "sales").Should().BeNull();
        SqlCrossDatabaseRule.Validate("SELECT 1 FROM \"SALES\".public.t", "PostgreSQL", "sales").Should().NotBeNull();

        // Snowflake folds an unquoted name, and an unquoted configured one, to upper case; a quoted one is exact.
        SqlCrossDatabaseRule.Validate("SELECT 1 FROM sales.public.t", "Snowflake", "sales").Should().BeNull();
        SqlCrossDatabaseRule.Validate("SELECT 1 FROM \"SALES\".public.t", "Snowflake", "sales").Should().BeNull();
        SqlCrossDatabaseRule.Validate("SELECT 1 FROM \"sales\".public.t", "Snowflake", "sales").Should().NotBeNull();
        SqlCrossDatabaseRule.Validate("SELECT 1 FROM \"Mixed\".public.t", "Snowflake", "\"Mixed\"").Should().BeNull();
        SqlCrossDatabaseRule.Validate("SELECT 1 FROM mixed.public.t", "Snowflake", "\"Mixed\"").Should().NotBeNull();

        // MySQL compares database names exactly: they are case-sensitive on most servers.
        SqlCrossDatabaseRule.Validate("SELECT 1 FROM `sales`.t", "MySQL", "sales").Should().BeNull();
        SqlCrossDatabaseRule.Validate("SELECT 1 FROM `SALES`.t", "MySQL", "sales").Should().Contain("another database");
        SqlCrossDatabaseRule.Validate("SELECT 1 FROM Sales.t", "MySQL", "sales").Should().Contain("another database");

        // SQL Server, SQLite, Databricks and BigQuery compare without regard to case.
        SqlCrossDatabaseRule.Validate("SELECT 1 FROM SALES.dbo.t", "MSSQL", "Sales").Should().BeNull();
        SqlCrossDatabaseRule.Validate("SELECT 1 FROM [sales].[dbo].[t]", "AzureSynapse", "Sales").Should().BeNull();
        SqlCrossDatabaseRule.Validate("SELECT 1 FROM MAIN.t", "SQLite", "main").Should().BeNull();
        SqlCrossDatabaseRule.Validate("SELECT 1 FROM MAIN.s.t", "databricks", "main").Should().BeNull();
    }

    [TestCase("PostgreSQL", "SELECT * FROM public.a WHERE EXISTS (SELECT 1 FROM hr.public.b)")]
    [TestCase("PostgreSQL", "WITH x AS (SELECT * FROM hr.public.b) SELECT * FROM x")]
    [TestCase("PostgreSQL", "SELECT * FROM public.a JOIN hr.public.b ON a.id = b.id")]
    [TestCase("PostgreSQL", "SELECT id FROM public.a UNION ALL SELECT id FROM hr.public.b")]
    [TestCase("PostgreSQL", "SELECT (SELECT max(id) FROM hr.public.b) AS m")]
    [TestCase("PostgreSQL", "SELECT * FROM (SELECT * FROM hr.public.b) d")]
    [TestCase("PostgreSQL", "SELECT CAST(x AS hr.public.money_type) FROM public.a")]
    [TestCase("MSSQL", "SELECT * FROM dbo.a CROSS APPLY HR.dbo.split(a.tags) s")]
    [TestCase("MSSQL", "DESCRIBE HR.dbo.salaries")]
    [TestCase("MySQL", "SELECT * FROM a WHERE id IN (SELECT id FROM hr.b)")]
    public void Rule_LooksAtEveryNameInTheStatement(string dialect, string sql)
    {
        SqlCrossDatabaseRule.Validate(sql, dialect, "sales").Should().Contain("another database");
    }

    [TestCase("SELECT * FROM INFORMATION_SCHEMA.TABLES", null, true)]
    [TestCase("SELECT * FROM `region-us`.INFORMATION_SCHEMA.JOBS", null, true)]
    [TestCase("SELECT * FROM `REGION-eu`.INFORMATION_SCHEMA.JOBS", "acme-prod", true)]
    [TestCase("SELECT * FROM `acme-prod`.INFORMATION_SCHEMA.SCHEMATA", "acme-prod", true)]
    [TestCase("SELECT * FROM `acme-dev`.INFORMATION_SCHEMA.SCHEMATA", "acme-prod", false)]
    [TestCase("SELECT * FROM `acme-dev.INFORMATION_SCHEMA.SCHEMATA`", "acme-prod", false)]
    [TestCase("SELECT * FROM sales.INFORMATION_SCHEMA.TABLES", "acme-prod", false)]
    [TestCase("SELECT * FROM sales.INFORMATION_SCHEMA.TABLES", null, false)]
    [TestCase("SELECT * FROM `acme-prod`.`region-us`.INFORMATION_SCHEMA.JOBS", "acme-prod", true)]
    [TestCase("SELECT * FROM a.b.c.INFORMATION_SCHEMA.JOBS", "a", false)]
    [TestCase("SELECT * FROM `acme-prod`.sales.INFORMATION_SCHEMA.TABLES", "acme-prod", true)]
    [TestCase("SELECT * FROM `acme-dev`.sales.INFORMATION_SCHEMA.TABLES", "acme-prod", false)]
    [TestCase("SELECT * FROM `acme-dev`.`region-us`.INFORMATION_SCHEMA.JOBS", "acme-prod", false)]
    [TestCase("SELECT * FROM `acme-dev.sales.events_*`", "acme-prod", false)]
    [TestCase("SELECT SAFE.DIVIDE(1, 0), NET.HOST('x')", null, true)]
    [TestCase("SELECT `acme-dev`.sales.salary(1)", "acme-prod", false)]
    public void Rule_KnowsBigQuerysNames(string sql, string? project, bool allowed)
    {
        var rejection = SqlCrossDatabaseRule.Validate(sql, "bigquery", project);

        if (allowed)
        {
            rejection.Should().BeNull();
            return;
        }

        rejection.Should().NotBeNull();
    }

    [TestCase("Snowflake", "SELECT * FROM IDENTIFIER('hr.public.salaries')")]
    [TestCase("Snowflake", "SELECT IDENTIFIER('salary') FROM public.t")]
    [TestCase("Snowflake", "SELECT * FROM TABLE('hr.public.salaries')")]
    [TestCase("databricks", "SELECT * FROM IDENTIFIER('hr.s.salaries')")]
    public void Rule_RefusesNamesResolvedOnlyWhenTheStatementRuns(string dialect, string sql)
    {
        SqlCrossDatabaseRule.Validate(sql, dialect, "SALES").Should().Contain("only when the statement runs");
    }

    [Test]
    public void Rule_AllowsSnowflakeTableFunctions()
    {
        SqlCrossDatabaseRule.Validate("SELECT * FROM TABLE(FLATTEN(input => parse_json('[1]')))", "Snowflake", null).Should().BeNull();
    }

    [Test]
    public void Rule_RefusesSqlItCannotRead_ButASaveTimeCheckLeavesItToTheExecutionGates()
    {
        SqlCrossDatabaseRule.Validate("SELECT FROM WHERE", "PostgreSQL", "sales").Should().Contain("could not be parsed");
        SqlCrossDatabaseRule.Validate("SELECT 1", null, "sales").Should().Contain("not known");
        SqlCrossDatabaseRule.ValidateIfParsed("SELECT FROM WHERE", "PostgreSQL", "sales").Should().BeNull();
        SqlCrossDatabaseRule.ValidateIfParsed("SELECT 1 FROM hr.public.t", null, "sales").Should().BeNull();
        SqlCrossDatabaseRule.ValidateIfParsed("SELECT 1 FROM hr.public.t", "PostgreSQL", "sales").Should().NotBeNull();
    }

    // The in-memory SQLite database that joins step results reaches nothing else, so the gate does not check its SQL; a
    // SQLite data source is checked like any other (its own file is attached as main).
    [Test]
    public void Gate_ChecksASqliteDataSource_ButNotTheInMemoryJoin()
    {
        var gate = TestSqlGate.Create();
        var settings = TestSqlGate.DefaultSettings();

        var join = gate.Evaluate(SqlGateRequest.ForInMemoryJoin("SELECT * FROM other.result1", settings));
        var foreign = gate.Evaluate(SqlGateRequest.FromSettings("SELECT * FROM other.orders", "SQLite", settings) with { ConfiguredDatabase = "main" });
        var own = gate.Evaluate(SqlGateRequest.FromSettings("SELECT * FROM main.orders", "SQLite", settings) with { ConfiguredDatabase = "main" });

        join.Blocked.Should().BeFalse();
        foreign.Blocked.Should().BeTrue();
        foreign.Verdicts.Schema.Code.Should().Be(SqlGateCodes.CrossDatabase);
        own.Blocked.Should().BeFalse();
        SqlCrossDatabaseRule.Validate("SELECT * FROM main.result1", "SQLite", null).Should().Contain("no database configured");
    }

    [TestCase(DataSourceType.Database, DatabaseEngineType.PostgreSQL, "Host=db;DB=sales;Username=reader", "sales")]
    [TestCase(DataSourceType.Database, DatabaseEngineType.MSSQL, "Server=db;Database=Sales;User Id=reader", "Sales")]
    [TestCase(DataSourceType.Database, DatabaseEngineType.MySQL, "Server=db;Initial Catalog=sales;Uid=reader", "sales")]
    [TestCase(DataSourceType.Database, DatabaseEngineType.PostgreSQL, "Host=db;Username=reader", null)]
    [TestCase(DataSourceType.Database, DatabaseEngineType.SQLite, "Data Source=/data/app.db", DataSourceConfiguredDatabase.SqliteMain)]
    [TestCase(DataSourceType.Database, DatabaseEngineType.MSSQL, "not a connection string ;;= =", null)]
    [TestCase(DataSourceType.Database, DatabaseEngineType.MSSQL, "Server=db;Database='Sales", DataSourceConfiguredDatabase.Undetermined)]
    [TestCase(DataSourceType.Database, DatabaseEngineType.PostgreSQL, "", DataSourceConfiguredDatabase.Undetermined)]
    [TestCase(DataSourceType.Databricks, null, """{"Host":"h","HttpPath":"/sql/1.0/warehouses/w","Token":"t"}""", null)]
    [TestCase(DataSourceType.BigQuery, null, "not json", DataSourceConfiguredDatabase.Undetermined)]
    [TestCase(DataSourceType.BigQuery, null, "[\"acme-prod\"]", DataSourceConfiguredDatabase.Undetermined)]
    [TestCase(DataSourceType.Api, null, """{"baseUrl":"https://example.test"}""", null)]
    public void ConfiguredDatabase_IsNullWhenNoneIsConfigured_AndUndeterminedWhenItCannotBeRead(DataSourceType type, DatabaseEngineType? engine, string connectionData, string? expected)
    {
        DataSourceConfiguredDatabase.Of(type, engine, connectionData).Should().Be(expected);
    }

    [Test]
    public void Resolver_ReadsTheDecryptedConnection_AndAllowsNoQualifierWhenItCannotBeRead()
    {
        var dataSource = new DataSource { Id = 12, Name = "warehouse", DataSourceType = DataSourceType.Database, DatabaseEngineType = DatabaseEngineType.PostgreSQL, EncryptedConnectionData = "enc" };
        var readable = new Mock<IDataSourceConnectionResolver>();
        readable.Setup(x => x.GetConnectionString(dataSource)).Returns("Host=db;Database=sales");
        var unreadable = new Mock<IDataSourceConnectionResolver>();
        unreadable.Setup(x => x.GetConnectionString(dataSource)).Throws(new InvalidOperationException("Host-Password=hunter2"));
        var logs = new LogRecorder();

        new ConfiguredDatabaseResolver(readable.Object, logs.For<ConfiguredDatabaseResolver>()).Resolve(dataSource).Should().Be("sales");
        new ConfiguredDatabaseResolver(unreadable.Object, logs.For<ConfiguredDatabaseResolver>()).Resolve(dataSource).Should().Be(DataSourceConfiguredDatabase.Undetermined);

        var entry = logs.Entries.Should().ContainSingle().Subject;
        entry.Message.Should().Contain("12").And.Contain(nameof(InvalidOperationException)).And.NotContain("hunter2");
    }

    private static SqlGateRequest Request(string sql, QualifierCase dialect, bool enforceReadOnly)
    {
        return SqlGateRequest.FromSettings(sql, dialect.Dialect, TestSqlGate.DefaultSettings()) with
        {
            EnforceReadOnly = enforceReadOnly,
            ConfiguredDatabase = dialect.Configured,
            MaxRows = 10
        };
    }

    private static QueryData NewQuery(string sql)
    {
        return new QueryData
        {
            Name = "orders",
            Steps = [Step(sql)]
        };
    }

    private static QueryStepData Step(string sql)
    {
        return new QueryStepData { StepOrder = 1, Name = "step", SqlValue = sql, DataSourceId = DataSourceId };
    }

    private static QueryService Service(
        QualifierCase dialect,
        List<object> saved,
        IQueryVersionService? versions = null,
        Query? stored = null,
        List<QueryStep>? steps = null)
    {
        var dataSource = new DataSource
        {
            Id = DataSourceId,
            Name = "warehouse",
            DataSourceType = dialect.Type,
            DatabaseEngineType = dialect.Engine,
            EncryptedConnectionData = "enc"
        };
        foreach (var step in steps ?? [])
        {
            step.DataSource = dataSource;
        }

        var connections = new Mock<IDataSourceConnectionResolver>();
        connections
            .Setup(x => x.GetConnectionString(It.IsAny<DataSource>()))
            .Returns(dialect.ConnectionData);
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object>
                {
                    [typeof(DataSource)] = RecordingBeaconContext.MemorySet<DataSource>([dataSource], saved),
                    [typeof(Query)] = RecordingBeaconContext.MemorySet(stored == null ? new List<Query>() : [stored], saved),
                    [typeof(QueryStep)] = RecordingBeaconContext.MemorySet(steps ?? [], saved)
                },
                saved));

        return new QueryService(
            factory.Object,
            connections.Object,
            Mock.Of<IManualQueryExecutionLogger>(),
            NullLogger<QueryService>.Instance,
            NullLoggerFactory.Instance,
            versions ?? Mock.Of<IQueryVersionService>(),
            new BeaconConfiguration(),
            Mock.Of<IBeaconUserContext>(),
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            Mock.Of<IHostDataSourceGuard>(x => x.Check(It.IsAny<DataSource>(), It.IsAny<string>()) == Allowed),
            TestProjectAccess.Actors(),
            TestProjectAccess.ExecutionAllowed(),
            new ConfiguredDatabaseResolver(connections.Object, NullLogger<ConfiguredDatabaseResolver>.Instance));
    }

    private static readonly HostPolicyResult Allowed = new(true, null, []);

    private static ConcurrentDictionary<DatabaseEngineType, Func<string, DbConnection>> Registry()
    {
        return (ConcurrentDictionary<DatabaseEngineType, Func<string, DbConnection>>)typeof(DbConnectionFactory)
            .GetField("_factories", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
    }

    private static IEnumerable<QualifierCase> Cases()
    {
        yield return new QualifierCase(
            "PostgreSQL", DataSourceType.Database, DatabaseEngineType.PostgreSQL, "PostgreSQL", "database",
            "Host=db;Database=sales;Username=reader", "sales",
            "SELECT id FROM sales.public.orders", "SELECT id FROM hr.public.orders", "SELECT id FROM public.orders",
            "SELECT hr.public.salary(1)", "SELECT id FROM a.b.c.d");
        yield return new QualifierCase(
            "SQL Server", DataSourceType.Database, DatabaseEngineType.MSSQL, "MSSQL", "database",
            "Server=db;Initial Catalog=Sales;User Id=reader", "Sales",
            "SELECT id FROM Sales.dbo.orders", "SELECT id FROM HR.dbo.orders", "SELECT id FROM dbo.orders",
            "SELECT HR.dbo.salary(1)", "SELECT id FROM a.b.c.d");
        yield return new QualifierCase(
            "Azure Synapse", DataSourceType.Database, DatabaseEngineType.AzureSynapse, "AzureSynapse", "database",
            "Server=db;Database=Sales;User Id=reader", "Sales",
            "SELECT id FROM [Sales].[dbo].[orders]", "SELECT id FROM [HR].[dbo].[orders]", "SELECT id FROM orders",
            "SELECT HR.dbo.salary(1)", "SELECT id FROM a.b.c.d");
        yield return new QualifierCase(
            "MySQL", DataSourceType.Database, DatabaseEngineType.MySQL, "MySQL", "database",
            "Server=db;Database=sales;Uid=reader", "sales",
            "SELECT id FROM `sales`.`orders`", "SELECT id FROM hr.orders", "SELECT id FROM orders",
            "SELECT hr.salary(1)", "SELECT id FROM a.b.c");
        yield return new QualifierCase(
            "Snowflake", DataSourceType.Database, DatabaseEngineType.Snowflake, "Snowflake", "database",
            "account=acme;user=reader;db=SALES", "SALES",
            "SELECT id FROM sales.public.orders", "SELECT id FROM hr.public.orders", "SELECT id FROM public.orders",
            "SELECT hr.public.salary(1)", "SELECT id FROM a.b.c.d");
        yield return new QualifierCase(
            "Databricks", DataSourceType.Databricks, null, "databricks", "catalog",
            """{"Host":"h","HttpPath":"/sql/1.0/warehouses/w","Token":"t","Catalog":"main"}""", "main",
            "SELECT id FROM main.sales.orders", "SELECT id FROM hive_metastore.sales.orders", "SELECT id FROM sales.orders",
            "SELECT system.ai.python_exec('x')", "SELECT id FROM a.b.c.d");
        yield return new QualifierCase(
            "SQLite", DataSourceType.Database, DatabaseEngineType.SQLite, "SQLite", "database",
            "Data Source=/data/app.db", DataSourceConfiguredDatabase.SqliteMain,
            "SELECT id FROM main.orders", "SELECT id FROM other.orders", "SELECT id FROM orders",
            "SELECT other.salary(1)", "SELECT id FROM a.b.c");
        yield return new QualifierCase(
            "BigQuery", DataSourceType.BigQuery, null, "bigquery", "project",
            """{"ProjectId":"acme-prod","DatasetId":"sales"}""", "acme-prod",
            "SELECT id FROM `acme-prod.sales.orders`", "SELECT id FROM `acme-dev.sales.orders`", "SELECT id FROM sales.orders",
            "SELECT `acme-dev`.sales.salary(1)", "SELECT id FROM `a.b.c.d`");
    }

    /// <param name="Configured">The database (catalog, project) <paramref name="ConnectionData"/> configures.</param>
    public sealed record QualifierCase(
        string Name,
        DataSourceType Type,
        DatabaseEngineType? Engine,
        string Dialect,
        string Noun,
        string ConnectionData,
        string Configured,
        string Matching,
        string Foreign,
        string Local,
        string ForeignFunction,
        string TooManyParts);

    private sealed class ConnectionReachedException : Exception;
}
