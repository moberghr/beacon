using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// A SELECT that reaches another server or storage location is not read-only for the data source it runs on: table
/// functions and calls that open a connection, linked-server names and file path references are refused, while ordinary
/// names in each engine's own naming scheme — T-SQL's database.schema.table included — still pass.
/// </summary>
[TestFixture]
public class SqlReadOnlyValidatorRemoteAccessTests
{
    private const string OpenQueryUpdate = "SELECT * FROM OPENQUERY(LNK,'UPDATE[dbo].[accounts] SET balance=0 SELECT 1 AS a')";

    private SqlReadOnlyAstValidator _validator = null!;

    [SetUp]
    public void SetUp()
    {
        _validator = new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance);
    }

    [TestCase(OpenQueryUpdate, "MSSQL")]
    [TestCase("SELECT * FROM OPENQUERY(LNK,'UPDATE dbo.accounts SET balance=0; SELECT 1 AS a')", "MSSQL")]
    [TestCase("SELECT * FROM OPENROWSET('MSOLEDBSQL','Server=remote.example;Trusted_Connection=yes;','SELECT 1') AS a", "MSSQL")]
    [TestCase("SELECT * FROM OPENXML(@h, '/root', 1)", "MSSQL")]
    [TestCase("SELECT * FROM openquery(LNK, 'SELECT 1')", "AzureSynapse")]
    [TestCase("SELECT * FROM sys.fn_xe_file_target_read_file('\\\\host\\share\\*.xel', NULL, NULL, NULL)", "MSSQL")]
    [TestCase("SELECT master.dbo.xp_dirtree('\\\\host\\share')", "MSSQL")]
    [TestCase("SELECT dblink_exec('host=10.0.0.5 dbname=core','TRUNCATE accounts')", "PostgreSQL")]
    [TestCase("SELECT dblink_connect('c','host=169.254.169.254 port=80 dbname=x')", "PostgreSQL")]
    [TestCase("SELECT public.dblink_send_query('c', 'SELECT 1')", "PostgreSQL")]
    [TestCase("SELECT * FROM read_files('s3://b/p/', format => 'csv')", "databricks")]
    [TestCase("SELECT * FROM cloud_files('s3://b/p/', 'json')", "databricks")]
    [TestCase("SELECT * FROM read_kafka(bootstrapServers => 'x', subscribe => 'y')", "databricks")]
    [TestCase("SELECT http_request(conn => 'c', method => 'GET', path => '/')", "databricks")]
    [TestCase("SELECT * FROM EXTERNAL_QUERY('p.eu.c','SELECT 1')", "bigquery")]
    public void Validate_CrossConnectionFunction_IsRejected(string sql, string dialect)
    {
        _validator.Validate(sql, dialect).Should().StartWith("The function ");
    }

    [TestCase("SELECT name FROM LNK.core.dbo.customers", "MSSQL")]
    [TestCase("SELECT name FROM [LNK].[core].[dbo].[customers]", "MSSQL")]
    [TestCase("SELECT c.name FROM dbo.orders o JOIN LNK.core.dbo.customers c ON c.id = o.customer_id", "AzureSynapse")]
    [TestCase("SELECT * FROM (SELECT name FROM LNK.core.dbo.customers) x", "MSSQL")]
    [TestCase("SELECT LNK.core.dbo.f(1)", "MSSQL")]
    [TestCase("SELECT * FROM LNK.core.dbo.tvf(1)", "AzureSynapse")]
    public void Validate_TSqlLinkedServerName_IsRejected(string sql, string dialect)
    {
        _validator.Validate(sql, dialect).Should().StartWith("Names with four or more parts are not allowed");
    }

    [TestCase("SELECT * FROM json.`abfss://raw@acct.dfs.core.windows.net/pii/`")]
    [TestCase("SELECT * FROM delta.`/mnt/raw/accounts`")]
    [TestCase("SELECT * FROM parquet.`s3://bucket/events/`")]
    [TestCase("SELECT * FROM parquet.`events`")]
    [TestCase("SELECT * FROM `s3://bucket/x`")]
    [TestCase("SELECT * FROM `abfss://raw@acct.dfs.core.windows.net/pii/`")]
    [TestCase("SELECT * FROM `my/schema`.t")]
    [TestCase("SELECT a.id FROM main.sales.orders a JOIN csv.`s3://bucket/x.csv` b ON a.id = b.id")]
    public void Validate_DatabricksPathReference_IsRejected(string sql)
    {
        _validator.Validate(sql, "databricks").Should().StartWith("File and storage path references");
    }

    // Each engine's own naming scheme stays allowed: two-part schema.table everywhere, Unity Catalog's
    // catalog.schema.table, BigQuery's project.dataset.table and Snowflake's database.schema.table. Those reach only
    // what the data source's credential can already read on the same service, not another server.
    [TestCase("SELECT name FROM dbo.customers", "MSSQL")]
    [TestCase("SELECT name FROM [dbo].[customers] WITH (NOLOCK)", "MSSQL")]
    [TestCase("SELECT * FROM STRING_SPLIT('a,b', ',')", "MSSQL")]
    [TestCase("SELECT name FROM otherdb.dbo.customers", "MSSQL")]
    [TestCase("SELECT otherdb.dbo.f(1)", "MSSQL")]
    [TestCase("SELECT dbo.customers.name FROM dbo.customers", "MSSQL")]
    [TestCase("SELECT * FROM #tmp", "MSSQL")]
    [TestCase("SELECT * FROM INFORMATION_SCHEMA.TABLES", "MSSQL")]
    [TestCase("WITH c AS (SELECT * FROM otherdb.dbo.t) SELECT * FROM c JOIN db2.dbo.u ON 1 = 1", "AzureSynapse")]
    [TestCase("SELECT * FROM `text`.`orders`", "databricks")]
    [TestCase("SELECT * FROM `csv`.`events`", "databricks")]
    [TestCase("SELECT x FROM UNNEST([1, 2, 3]) AS x", "bigquery")]
    [TestCase("SELECT [STRUCT(1 AS a, 'x' AS b)] AS s, CAST(NULL AS ARRAY<STRUCT<a INT64> >) AS t", "bigquery")]
    [TestCase("SELECT '\\d' AS r, 1 AS n # comment", "bigquery")]
    [TestCase("SELECT $1, col:path::string FROM t", "Snowflake")]
    [TestCase("SELECT f.value FROM t, LATERAL FLATTEN(input => t.arr) f // comment", "Snowflake")]
    [TestCase("SELECT name FROM public.customers", "PostgreSQL")]
    [TestCase("SELECT * FROM generate_series(1, 10)", "PostgreSQL")]
    [TestCase("SELECT * FROM main.sales.orders", "databricks")]
    [TestCase("SELECT * FROM `my-catalog`.`my schema`.`my-table`", "databricks")]
    [TestCase("SELECT * FROM `proj.dataset.table`", "bigquery")]
    [TestCase("SELECT * FROM proj.dataset.orders", "bigquery")]
    [TestCase("SELECT * FROM analytics.public.orders", "Snowflake")]
    [TestCase("SELECT * FROM shop.orders", "MySQL")]
    public void Validate_OrdinaryQualifiedNames_Pass(string sql, string dialect)
    {
        _validator.Validate(sql, dialect).Should().BeNull();
    }

    // The parser cannot read a Snowflake stage reference, so it fails closed.
    [Test]
    public void Validate_SnowflakeStageReference_IsRejectedAsUnparseable()
    {
        _validator.Validate("SELECT $1 FROM @stage", "Snowflake").Should().StartWith("Could not verify this SQL is read-only");
    }

    [Test]
    public void Gate_OpenQueryWrite_IsBlockedByTheReadOnlyStage()
    {
        var catalog = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["t"] = new(StringComparer.OrdinalIgnoreCase) { "x" }
        };
        var request = SqlGateRequest.FromSettings(
            "SELECT * FROM OPENQUERY(LNK,'UPDATE[dbo].[t] SET x=1 SELECT 1 AS a')",
            "MSSQL",
            TestSqlGate.DefaultSettings()) with
        {
            EnforceReadOnly = true,
            Catalog = catalog,
            BlockOnSchemaFailure = true
        };

        var report = TestSqlGate.Create().Evaluate(request);

        report.Blocked.Should().BeTrue();
        report.Verdicts.ReadOnly.Status.Should().Be(SqlGateStatus.Fail);
        report.Verdicts.ReadOnly.Code.Should().Be(SqlGateCodes.Ast);
    }

    [Test]
    public async Task AddQueryStep_OpenQueryOnMssqlSource_IsRejectedAndNotPersisted()
    {
        var context = new SingleDataSourceContext();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);
        var service = new QueryService(
            factory.Object,
            Mock.Of<Beacon.Core.HostData.IDataSourceConnectionResolver>(),
            Mock.Of<IManualQueryExecutionLogger>(),
            NullLogger<QueryService>.Instance,
            NullLoggerFactory.Instance,
            Mock.Of<IQueryVersionService>(),
            null!,
            Mock.Of<IBeaconUserContext>(),
            _validator,
            Mock.Of<Beacon.Core.HostData.IHostDataSourceGuard>());
        var stepData = new QueryStepData
        {
            Name = "step",
            SqlValue = OpenQueryUpdate,
            DataSourceId = 1,
            StepOrder = 1
        };

        var act = () => service.AddQueryStep(1, stepData, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("The function OPENQUERY*");
        context.SavedStepSql.Should().BeEmpty();
    }

    /// <summary>
    /// BeaconContext with one MSSQL data source (id 1) backed by the async-queryable doubles, recording staged
    /// QuerySteps instead of saving them.
    /// </summary>
    private sealed class SingleDataSourceContext : BeaconContext
    {
        private static readonly DbContextOptions<SingleDataSourceContext> Options =
            new DbContextOptionsBuilder<SingleDataSourceContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        public SingleDataSourceContext() : base(Options, "beacon") { }

        public List<string> SavedStepSql { get; } = [];

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            return typeof(TEntity) == typeof(DataSource)
                ? (DbSet<TEntity>)(object)DataSourceSet()
                : base.Set<TEntity>();
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var added = ChangeTracker.Entries<QueryStep>()
                .Where(x => x.State == EntityState.Added)
                .Select(x => x.Entity.SqlValue)
                .ToList();
            SavedStepSql.AddRange(added);

            return Task.FromResult(added.Count);
        }

        private static DbSet<DataSource> DataSourceSet()
        {
            var data = new List<DataSource>
            {
                new()
                {
                    Id = 1,
                    Name = "core",
                    DataSourceType = DataSourceType.Database,
                    EncryptedConnectionData = "unused",
                    DatabaseEngineType = DatabaseEngineType.MSSQL
                }
            }.AsQueryable();
            var set = new Mock<DbSet<DataSource>>();
            set.As<IAsyncEnumerable<DataSource>>()
                .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
                .Returns(() => new TestAsyncEnumerator<DataSource>(data.GetEnumerator()));
            set.As<IQueryable<DataSource>>()
                .Setup(x => x.Provider)
                .Returns(new TestAsyncQueryProvider<DataSource>(data.Provider));
            set.As<IQueryable<DataSource>>().Setup(x => x.Expression).Returns(data.Expression);
            set.As<IQueryable<DataSource>>().Setup(x => x.ElementType).Returns(data.ElementType);
            set.As<IQueryable<DataSource>>().Setup(x => x.GetEnumerator()).Returns(() => data.GetEnumerator());

            return set.Object;
        }
    }
}
