using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// A query step's SQL passes the execution gate and, where Beacon holds the source's metadata, reads only columns its tables
/// have before it is stored. In-memory sets, no database (§4.7).
/// </summary>
[TestFixture]
public class StepSqlSaveCheckTests
{
    private const int DataSourceId = 4;

    [Test]
    public async Task KnownTablesAndColumns_MayBeStored()
    {
        var check = Check(Orders());

        var rejection = await check.RejectionAsync(DataSourceId, "SELECT o.id, o.status FROM public.orders o WHERE o.id = {id}", ["{id}"], CancellationToken.None);

        rejection.Should().BeNull();
    }

    [TestCase("SELECT missing_column FROM orders")]
    [TestCase("SELECT o.id FROM public.orders o WHERE o.missing_column = {id}")]
    public async Task AColumnTheTableDoesNotHave_IsRefused(string sql)
    {
        var check = Check(Orders());

        var rejection = await check.RejectionAsync(DataSourceId, sql, ["{id}"], CancellationToken.None);

        rejection.Should().StartWith("Column 'missing_column' does not exist");
    }

    [Test]
    public async Task ASourceWithoutMetadata_IsCheckedByTheGateAlone()
    {
        var check = Check([]);

        var rejection = await check.RejectionAsync(DataSourceId, "SELECT id FROM anything", [], CancellationToken.None);

        rejection.Should().BeNull();
    }

    [TestCase("DELETE FROM orders")]
    [TestCase("SELECT 1 AS id /*!50000 DELETE FROM orders */")]
    [TestCase("/* tidy up */ DELETE FROM orders")]
    [TestCase("SELECT FROM WHERE")]
    [TestCase("SELECT id FROM orders; SELECT 2")]
    public async Task SqlThatCouldNeverRun_IsRefused(string sql)
    {
        var check = Check(Orders());

        var rejection = await check.RejectionAsync(DataSourceId, sql, [], CancellationToken.None);

        rejection.Should().NotBeNull();
    }

    // Comments are prose: a step that starts with one, or names a write in one, is judged by its statement.
    [TestCase("-- Orders of the day\nSELECT id FROM orders")]
    [TestCase("/* delete old rows first */ SELECT id FROM orders")]
    [TestCase("SELECT id /* never update this */ FROM orders")]
    public async Task ACommentedStep_IsSaved(string sql)
    {
        var check = Check(Orders());

        var rejection = await check.RejectionAsync(DataSourceId, sql, [], CancellationToken.None);

        rejection.Should().BeNull();
    }

    [Test]
    public async Task AMissingDataSource_IsRefused()
    {
        var check = Check(Orders());

        var rejection = await check.RejectionAsync(99, "SELECT id FROM orders", [], CancellationToken.None);

        rejection.Should().Be("Data source 99 not found.");
    }

    private static StepSqlSaveCheck Check(List<DatabaseMetadata> metadata)
    {
        var dataSource = new DataSource
        {
            Id = DataSourceId,
            Name = "sales",
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = DatabaseEngineType.PostgreSQL,
            EncryptedConnectionData = "encrypted"
        };
        var saved = new List<object>();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object>
                {
                    [typeof(DataSource)] = RecordingBeaconContext.MemorySet([dataSource], saved),
                    [typeof(DatabaseMetadata)] = RecordingBeaconContext.MemorySet(metadata, saved)
                },
                saved));

        return new StepSqlSaveCheck(factory.Object, TestSqlGate.Create(), TestConfiguredDatabases.None);
    }

    private static List<DatabaseMetadata> Orders()
    {
        var table = new DatabaseMetadata { DataSourceId = DataSourceId, SchemaName = "public", TableName = "orders" };
        table.Columns =
        [
            new ColumnMetadata { ColumnName = "id", DataType = "integer", DatabaseMetadata = table },
            new ColumnMetadata { ColumnName = "status", DataType = "text", DatabaseMetadata = table }
        ];

        return [table];
    }
}
