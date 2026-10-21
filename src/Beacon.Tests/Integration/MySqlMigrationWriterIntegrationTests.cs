using System.Globalization;
using System.Text.Json;
using Beacon.Connector.MySql;
using Beacon.Core.DataMigration;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MySql.Data.MySqlClient;
using NUnit.Framework;

namespace Beacon.Tests.Integration;

/// <summary>
/// The MySQL migration writer against a real server: Truncate, Insert and Upsert (composite and all-key tables), NULLs for
/// missing values, every processed row counted once, rows crossing a command boundary in one transaction, a Truncate
/// rolled back with the transaction when a row fails, a Truncate refused on a table another table references, and the
/// values sources produce (dates and times, GUIDs, booleans, bytes, JSON, unsigned and non-ASCII values) stored as the
/// column expects them. Set <see cref="ConnectionStringEnvVar"/> to a login that may create a
/// database; the tests work in their own database (the writer's schema), dropped afterwards. Without it the tests are
/// inconclusive.
/// </summary>
[TestFixture]
public class MySqlMigrationWriterIntegrationTests
{
    public const string ConnectionStringEnvVar = "BEACON_TEST_MYSQL_CONNECTION_STRING";

    private string? _connectionString;
    private string _database = null!;
    private MySqlMigrationBulkWriter _writer = null!;

    [SetUp]
    public async Task SetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvVar);
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            Assert.Inconclusive($"{ConnectionStringEnvVar} is not set.");
        }

        _database = $"beacon_mw_{Guid.NewGuid():N}";
        _writer = new MySqlMigrationBulkWriter(NullLogger<MySqlMigrationBulkWriter>.Instance);
        await ExecuteAsync(
            $"CREATE DATABASE `{_database}`",
            $"CREATE TABLE `{_database}`.`Orders` (tenant int NOT NULL, id int NOT NULL, total decimal(18, 2) NULL, note varchar(100) NULL, PRIMARY KEY (tenant, id)) ENGINE = InnoDB",
            $"CREATE TABLE `{_database}`.links (a int NOT NULL, b int NOT NULL, PRIMARY KEY (a, b)) ENGINE = InnoDB",
            $"INSERT INTO `{_database}`.`Orders` VALUES (1, 1, 10, 'old'), (1, 2, 20, 'keep')",
            $"INSERT INTO `{_database}`.links VALUES (1, 1)");
    }

    [TearDown]
    public async Task TearDown()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return;
        }

        await ExecuteAsync($"DROP DATABASE IF EXISTS `{_database}`");
    }

    [Test]
    public async Task Truncate_ReplacesEveryRow()
    {
        await WriteAsync("Orders", async target =>
        {
            await _writer.ClearAsync(target, CancellationToken.None);
            var written = await _writer.InsertAsync(target, Rows(Order(2, 1, 5m, "new")), CancellationToken.None);
            written.Should().Be(1);
        });

        (await OrdersAsync()).Should().Equal("2|1|5.00|new");
    }

    [Test]
    public async Task Insert_AddsRows_WritingNullForNullsAndMissingValues()
    {
        var rows = new MigrationRows(
            ["tenant", "id", "total", "note"],
            [
                new Dictionary<string, object?> { ["tenant"] = 3, ["id"] = 1, ["total"] = null, ["note"] = "a" },
                new Dictionary<string, object?> { ["tenant"] = 3, ["id"] = 2 }
            ]);

        await WriteAsync("Orders", async target => (await _writer.InsertAsync(target, rows, CancellationToken.None)).Should().Be(2));

        (await OrdersAsync()).Should().Equal("1|1|10.00|old", "1|2|20.00|keep", "3|1|<null>|a", "3|2|<null>|<null>");
    }

    // MySQL reports 2 affected rows for an updated row; the writer counts each processed row once.
    [Test]
    public async Task Upsert_UpdatesRowsWhoseCompositeKeyExists_AndInsertsTheRest_CountingEachRowOnce()
    {
        await WriteAsync("Orders", async target =>
        {
            var written = await _writer.UpsertAsync(target, Rows(Order(1, 1, 11m, "updated"), Order(1, 3, 30m, "added")), ["tenant", "id"], CancellationToken.None);
            written.Should().Be(2);
        });

        (await OrdersAsync()).Should().Equal("1|1|11.00|updated", "1|2|20.00|keep", "1|3|30.00|added");
    }

    [Test]
    public async Task Upsert_WritesNullOverAnExistingValue()
    {
        var rows = new MigrationRows(["tenant", "id", "note"], [new Dictionary<string, object?> { ["tenant"] = 1, ["id"] = 2, ["note"] = null }]);

        await WriteAsync("Orders", async target => (await _writer.UpsertAsync(target, rows, ["tenant", "id"], CancellationToken.None)).Should().Be(1));

        (await OrdersAsync()).Should().Equal("1|1|10.00|old", "1|2|20.00|<null>");
    }

    [Test]
    public async Task Upsert_IntoAnAllKeyTable_InsertsNewRows_AndLeavesExistingOnes()
    {
        var rows = new MigrationRows(
            ["a", "b"],
            [
                new Dictionary<string, object?> { ["a"] = 1, ["b"] = 1 },
                new Dictionary<string, object?> { ["a"] = 1, ["b"] = 2 }
            ]);

        await WriteAsync("links", async target => (await _writer.UpsertAsync(target, rows, ["a", "b"], CancellationToken.None)).Should().Be(2));

        (await RowsAsync($"SELECT a, b FROM `{_database}`.links ORDER BY a, b")).Should().Equal("1|1", "1|2");
    }

    // More rows than one command carries: two commands, one transaction.
    [Test]
    public async Task InsertAndUpsert_AcrossACommandBoundary_WriteEveryRow()
    {
        var count = MySqlMigrationSql.MaxRowsPerCommand + 5;
        var inserted = Rows(Enumerable.Range(1, count).Select(x => Order(7, x, x, $"n{x}")).ToArray());
        var upserted = Rows(Enumerable.Range(1, count).Select(x => Order(7, x, x, $"u{x}")).ToArray());

        await WriteAsync("Orders", async target =>
        {
            (await _writer.InsertAsync(target, inserted, CancellationToken.None)).Should().Be(count);
            (await _writer.UpsertAsync(target, upserted, ["tenant", "id"], CancellationToken.None)).Should().Be(count);
        });

        (await ScalarAsync($"SELECT count(*) FROM `{_database}`.`Orders` WHERE tenant = 7 AND note LIKE 'u%'")).Should().Be((long)count);
    }

    // DELETE stays inside the transaction: a failed write rolls the whole run back, the clearing with it.
    [Test]
    public async Task Truncate_IsRolledBack_WhenARowFails()
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var target = new MigrationWriteTarget(connection, transaction, _database, "Orders", 30);

        await _writer.ClearAsync(target, CancellationToken.None);
        var failing = () => _writer.InsertAsync(target, Rows(Order(5, 1, 1m, "ok"), Order(5, 1, 2m, "duplicate key")), CancellationToken.None);
        await failing.Should().ThrowAsync<MySqlException>();
        await transaction.RollbackAsync();

        (await OrdersAsync()).Should().Equal("1|1|10.00|old", "1|2|20.00|keep");
    }

    // DELETE would cascade into the referencing rows where TRUNCATE refuses: the run is refused before anything is deleted.
    [Test]
    public async Task Truncate_OfATableAnotherTableReferences_IsRefused_LeavingBothTablesIntact()
    {
        await ExecuteAsync(
            $"CREATE TABLE `{_database}`.parents (id int NOT NULL PRIMARY KEY) ENGINE = InnoDB",
            $"CREATE TABLE `{_database}`.children (id int NOT NULL PRIMARY KEY, parent_id int NOT NULL, FOREIGN KEY (parent_id) REFERENCES `{_database}`.parents (id) ON DELETE CASCADE) ENGINE = InnoDB",
            $"INSERT INTO `{_database}`.parents VALUES (1), (2)",
            $"INSERT INTO `{_database}`.children VALUES (10, 1), (20, 2)");

        var truncate = () => WriteAsync("parents", target => _writer.ClearAsync(target, CancellationToken.None));

        await truncate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*parents*foreign key*");
        (await RowsAsync($"SELECT id FROM `{_database}`.parents ORDER BY id")).Should().Equal("1", "2");
        (await RowsAsync($"SELECT id, parent_id FROM `{_database}`.children ORDER BY id")).Should().Equal("10|1", "20|2");
    }

    // A table's reference to itself does not stop it (TRUNCATE allows it too); without a schema the connection's database is checked.
    [Test]
    public async Task Truncate_OfASelfReferencingTable_InTheConnectionsDatabase_DeletesEveryRow()
    {
        await ExecuteAsync(
            $"CREATE TABLE `{_database}`.nodes (id int NOT NULL PRIMARY KEY, parent_id int NULL, FOREIGN KEY (parent_id) REFERENCES `{_database}`.nodes (id) ON DELETE CASCADE) ENGINE = InnoDB",
            $"INSERT INTO `{_database}`.nodes VALUES (1, NULL), (2, 1)");

        await WriteAsync("nodes", target => _writer.ClearAsync(target with { Schema = null }, CancellationToken.None), _database);

        (await ScalarAsync($"SELECT count(*) FROM `{_database}`.nodes")).Should().Be(0L);
    }

    // The values sources produce, bound as they are (or as the writer normalises them) and read back as the server stored them.
    [TestCaseSource(nameof(TypedValues))]
    public async Task AValue_IsStoredAsTheColumnExpectsIt(string columnType, object value, string readBack, string expected)
    {
        await ExecuteAsync($"CREATE TABLE `{_database}`.typed (id int NOT NULL PRIMARY KEY, v {columnType} NULL) ENGINE = InnoDB");
        var rows = new MigrationRows(["id", "v"], [new Dictionary<string, object?> { ["id"] = 1, ["v"] = value }]);

        await WriteAsync("typed", async target => (await _writer.InsertAsync(target, rows, CancellationToken.None)).Should().Be(1));

        (await RowsAsync($"SELECT {readBack} FROM `{_database}`.typed")).Should().Equal(expected);
    }

    private static IEnumerable<TestCaseData> TypedValues()
    {
        const string text = "CAST(v AS CHAR)";
        const string hex = "HEX(v)";

        yield return Typed("DateTime", "datetime(6)", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified).AddTicks(1_234_560), text, "2026-01-02 03:04:05.123456");
        yield return Typed("DateTimeOffset", "datetime(6)", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)).AddTicks(1_234_560), text, "2026-01-02 01:04:05.123456");
        yield return Typed("Guid", "char(36)", Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"), text, "0f8fad5b-d9cb-469f-a165-70867728950e");
        yield return Typed("bool", "tinyint(1)", true, text, "1");
        yield return Typed("byte[] blob", "blob", new byte[] { 0, 1, 2, 255 }, hex, "000102FF");
        yield return Typed("byte[] varbinary", "varbinary(16)", new byte[] { 0, 1, 2, 255 }, hex, "000102FF");
        yield return Typed("TimeSpan", "time(6)", new TimeSpan(1, 2, 3, 4, 5), text, "26:03:04.005000");
        yield return Typed("DateOnly", "date", new DateOnly(2026, 1, 2), text, "2026-01-02");
        yield return Typed("TimeOnly", "time(6)", new TimeOnly(13, 14, 15, 16), text, "13:14:15.016000");
        yield return Typed("ulong", "bigint unsigned", ulong.MaxValue, text, "18446744073709551615");
        yield return Typed("long", "bigint", long.MinValue, text, "-9223372036854775808");
        yield return Typed("double", "double", 1234.5678d, text, "1234.5678");
        yield return Typed("non-ASCII string", "text CHARACTER SET utf8mb4", "Čćžšđ — 日本語 🙂", text, "Čćžšđ — 日本語 🙂");
        yield return Typed("JsonElement object", "json", JsonDocument.Parse("""{"a":1,"b":[true,null,"x"]}""").RootElement.Clone(), text, """{"a": 1, "b": [true, null, "x"]}""");
        yield return Typed("JsonElement array", "json", JsonDocument.Parse("[1,2]").RootElement.Clone(), text, "[1, 2]");
        yield return Typed("JsonElement number", "decimal(10, 2)", JsonDocument.Parse("12.5").RootElement.Clone(), text, "12.50");
        yield return Typed("JsonElement true", "tinyint(1)", JsonDocument.Parse("true").RootElement.Clone(), text, "1");
        yield return Typed("JsonElement string", "varchar(50)", JsonDocument.Parse("\"héllo\"").RootElement.Clone(), text, "héllo");
    }

    private static TestCaseData Typed(string name, string columnType, object value, string readBack, string expected)
    {
        return new TestCaseData(columnType, value, readBack, expected).SetName($"AValue_IsStoredAsTheColumnExpectsIt({name})");
    }

    private async Task WriteAsync(string table, Func<MigrationWriteTarget, Task> write, string? database = null)
    {
        var connectionString = database == null
            ? _connectionString
            : new MySqlConnectionStringBuilder(_connectionString) { Database = database }.ConnectionString;
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await write(new MigrationWriteTarget(connection, transaction, _database, table, 30));
        await transaction.CommitAsync();
    }

    private static MigrationRows Rows(params Dictionary<string, object?>[] rows) => new(["tenant", "id", "total", "note"], rows);

    private static Dictionary<string, object?> Order(int tenant, int id, decimal total, string note) =>
        new() { ["tenant"] = tenant, ["id"] = id, ["total"] = total, ["note"] = note };

    private Task<List<string>> OrdersAsync() => RowsAsync($"SELECT tenant, id, total, note FROM `{_database}`.`Orders` WHERE tenant <> 7 ORDER BY tenant, id");

    private async Task<List<string>> RowsAsync(string sql)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            var values = Enumerable.Range(0, reader.FieldCount)
                .Select(x => reader.IsDBNull(x) ? "<null>" : Convert.ToString(reader.GetValue(x), CultureInfo.InvariantCulture));
            rows.Add(string.Join("|", values));
        }

        return rows;
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);

        return await command.ExecuteScalarAsync();
    }

    private async Task ExecuteAsync(params string[] statements)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        foreach (var statement in statements)
        {
            await using var command = new MySqlCommand(statement, connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
