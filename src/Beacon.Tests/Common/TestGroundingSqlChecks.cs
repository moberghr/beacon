using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Beacon.Tests.Common;

/// <summary>
/// <see cref="IGroundingSqlCheck"/> for tests: a stand-in that passes every SQL unchanged or refuses every SQL, and the real
/// check over in-memory sets (a PostgreSQL data source with an <c>orders</c> table) — no database (§4.7).
/// </summary>
internal static class TestGroundingSqlChecks
{
    public const int DataSourceId = 7;

    /// <summary>Passes every SQL as it is; its batch is itself.</summary>
    public static Mock<IGroundingSqlCheck> PassThrough()
    {
        var check = new Mock<IGroundingSqlCheck>();
        check
            .Setup(x => x.CheckAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, string sql, CancellationToken _) => GroundingSqlCheckResult.Allowed(sql));
        check
            .Setup(x => x.StartBatch())
            .Returns(() => check.Object);

        return check;
    }

    /// <summary>Refuses every SQL with <paramref name="reason"/>; its batch is itself.</summary>
    public static Mock<IGroundingSqlCheck> Refusing(string reason = "Only SELECT statements may be stored.")
    {
        var check = new Mock<IGroundingSqlCheck>();
        check
            .Setup(x => x.CheckAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GroundingSqlCheckResult.Refused(reason));
        check
            .Setup(x => x.StartBatch())
            .Returns(() => check.Object);

        return check;
    }

    /// <summary>
    /// The real check, on data source <see cref="DataSourceId"/> of <paramref name="engine"/>, whose metadata holds
    /// <c>public.orders (id, status, total)</c> — or nothing, without <paramref name="withCatalog"/>. <paramref name="onRead"/>
    /// is told about every context the check opens (one per read).
    /// </summary>
    public static IGroundingSqlCheck Real(DatabaseEngineType engine = DatabaseEngineType.PostgreSQL, Action? onRead = null, bool withCatalog = true)
    {
        return RealFor(DataSourceType.Database, engine, onRead, withCatalog);
    }

    /// <summary>As <see cref="Real"/>, on a data source of <paramref name="type"/> and <paramref name="engine"/> (none: no dialect).</summary>
    public static IGroundingSqlCheck RealFor(DataSourceType type, DatabaseEngineType? engine, Action? onRead = null, bool withCatalog = true)
    {
        var dataSource = new DataSource
        {
            Id = DataSourceId,
            Name = "sales",
            DataSourceType = type,
            DatabaseEngineType = engine,
            EncryptedConnectionData = "encrypted"
        };
        var table = new DatabaseMetadata { DataSourceId = DataSourceId, SchemaName = "public", TableName = "orders" };
        table.Columns =
        [
            new ColumnMetadata { ColumnName = "id", DataType = "integer", DatabaseMetadata = table },
            new ColumnMetadata { ColumnName = "status", DataType = "text", DatabaseMetadata = table },
            new ColumnMetadata { ColumnName = "total", DataType = "numeric", DatabaseMetadata = table }
        ];
        var saved = new List<object>();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .Callback(() => onRead?.Invoke())
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object>
                {
                    [typeof(DataSource)] = RecordingBeaconContext.MemorySet([dataSource], saved),
                    [typeof(DatabaseMetadata)] = RecordingBeaconContext.MemorySet(withCatalog ? [table] : new List<DatabaseMetadata>(), saved)
                },
                saved));

        return new GroundingSqlCheck(
            factory.Object,
            new StepSqlSaveCheck(factory.Object, TestSqlGate.Create(), TestConfiguredDatabases.None));
    }
}
