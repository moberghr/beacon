using System.Globalization;
using Beacon.Connector.SqlServer;
using Beacon.Core.DataMigration;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Beacon.Tests.Integration;

/// <summary>
/// The SQL Server migration writer against a real server: Truncate, Insert and Upsert (composite, all-key and identity
/// tables), NULLs for missing values and over defaults, identity values kept, insert triggers fired, only changed rows
/// updated (every matched row with an xml column), every processed row counted, chunks in one transaction, a duplicate key
/// in a chunk failing with the clean-up done, and a Truncate rolled back with the transaction when a row fails. Set <see cref="ConnectionStringEnvVar"/> to a login that may create a schema; the test
/// works in its own schema, dropped afterwards. Without it the tests are inconclusive.
/// </summary>
[TestFixture]
public class SqlServerMigrationWriterIntegrationTests
{
    public const string ConnectionStringEnvVar = "BEACON_TEST_SQLSERVER_CONNECTION_STRING";

    private string? _connectionString;
    private string _schema = null!;
    private SqlServerMigrationBulkWriter _writer = null!;

    [SetUp]
    public async Task SetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvVar);
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            Assert.Inconclusive($"{ConnectionStringEnvVar} is not set.");
        }

        _schema = $"beacon_mw_{Guid.NewGuid():N}";
        _writer = new SqlServerMigrationBulkWriter(NullLogger<SqlServerMigrationBulkWriter>.Instance);
        await ExecuteAsync(
            $"CREATE SCHEMA [{_schema}]",
            $"CREATE TABLE [{_schema}].[Orders] (tenant int NOT NULL, id int NOT NULL, total decimal(18, 2) NULL, note nvarchar(100) NULL, PRIMARY KEY (tenant, id))",
            $"CREATE TABLE [{_schema}].links (a int NOT NULL, b int NOT NULL, PRIMARY KEY (a, b))",
            $"CREATE TABLE [{_schema}].people (id int IDENTITY(1, 1) PRIMARY KEY, name nvarchar(50) NULL)",
            $"CREATE TABLE [{_schema}].docs (id int PRIMARY KEY, body xml NULL, title nvarchar(max) NULL)",
            $"CREATE TABLE [{_schema}].events (id int PRIMARY KEY, label nvarchar(20) NULL DEFAULT 'dflt')",
            $"CREATE TABLE [{_schema}].event_log (id int NOT NULL)",
            $"CREATE TRIGGER [{_schema}].events_logged ON [{_schema}].events AFTER INSERT AS INSERT INTO [{_schema}].event_log (id) SELECT id FROM inserted",
            $"INSERT INTO [{_schema}].[Orders] VALUES (1, 1, 10, 'old'), (1, 2, 20, 'keep')",
            $"INSERT INTO [{_schema}].links VALUES (1, 1)",
            $"INSERT INTO [{_schema}].people (name) VALUES ('first')",
            $"INSERT INTO [{_schema}].docs VALUES (1, '<a/>', 'old')");
    }

    [TearDown]
    public async Task TearDown()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return;
        }

        await ExecuteAsync(
            $"DROP TABLE IF EXISTS [{_schema}].[Orders]",
            $"DROP TABLE IF EXISTS [{_schema}].links",
            $"DROP TABLE IF EXISTS [{_schema}].people",
            $"DROP TABLE IF EXISTS [{_schema}].docs",
            $"DROP TABLE IF EXISTS [{_schema}].events",
            $"DROP TABLE IF EXISTS [{_schema}].event_log",
            $"DROP SCHEMA IF EXISTS [{_schema}]");
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

    [Test]
    public async Task Insert_KeepsTheGivenIdentityValues()
    {
        var rows = new MigrationRows(["id", "name"], [new Dictionary<string, object?> { ["id"] = 42, ["name"] = "kept" }]);

        await WriteAsync("people", async target => (await _writer.InsertAsync(target, rows, CancellationToken.None)).Should().Be(1));

        (await PeopleAsync()).Should().Equal("1|first", "42|kept");
    }

    [Test]
    public async Task Upsert_UpdatesRowsWhoseCompositeKeyExists_AndInsertsTheRest()
    {
        await WriteAsync("Orders", async target =>
        {
            var written = await _writer.UpsertAsync(target, Rows(Order(1, 1, 11m, "updated"), Order(1, 3, 30m, "added")), ["tenant", "id"], CancellationToken.None);
            written.Should().Be(2);
        });

        (await OrdersAsync()).Should().Equal("1|1|11.00|updated", "1|2|20.00|keep", "1|3|30.00|added");
    }

    // Only a row whose values changed is updated; NULLs compare as equal. Every row processed still counts as written.
    [Test]
    public async Task Upsert_LeavesUnchangedRowsAlone_ComparingNullsAsEqual_AndCountsEveryRowProcessed()
    {
        await ExecuteAsync(
            $"UPDATE [{_schema}].[Orders] SET note = NULL WHERE id = 2",
            $"ALTER TABLE [{_schema}].[Orders] ADD version rowversion");
        var versions = await RowsAsync($"SELECT CONVERT(bigint, version) FROM [{_schema}].[Orders] ORDER BY id");
        var rows = new MigrationRows(
            ["tenant", "id", "total", "note"],
            [
                new Dictionary<string, object?> { ["tenant"] = 1, ["id"] = 1, ["total"] = 10m, ["note"] = "old" },
                new Dictionary<string, object?> { ["tenant"] = 1, ["id"] = 2, ["total"] = 20m, ["note"] = null }
            ]);

        await WriteAsync("Orders", async target => (await _writer.UpsertAsync(target, rows, ["tenant", "id"], CancellationToken.None)).Should().Be(2));

        (await OrdersAsync()).Should().Equal("1|1|10.00|old", "1|2|20.00|<null>");
        (await RowsAsync($"SELECT CONVERT(bigint, version) FROM [{_schema}].[Orders] ORDER BY id")).Should().Equal(versions, "neither row was updated");
    }

    // xml cannot take part in EXCEPT: with an xml column the upsert updates every matched row instead.
    [Test]
    public async Task Upsert_IntoATableWithAnXmlColumn_UpdatesMatchedRows_AndInsertsTheRest()
    {
        var rows = new MigrationRows(
            ["id", "body", "title"],
            [
                new Dictionary<string, object?> { ["id"] = 1, ["body"] = "<b/>", ["title"] = "new" },
                new Dictionary<string, object?> { ["id"] = 2, ["body"] = "<c/>", ["title"] = "added" }
            ]);

        await WriteAsync("docs", async target => (await _writer.UpsertAsync(target, rows, ["id"], CancellationToken.None)).Should().Be(2));

        (await RowsAsync($"SELECT id, CONVERT(nvarchar(max), body), title FROM [{_schema}].docs ORDER BY id")).Should().Equal("1|<b/>|new", "2|<c/>|added");
    }

    // A NULL, or a value the row leaves out, is written as NULL, never replaced by the column's default; insert triggers fire.
    [Test]
    public async Task Insert_WritesNullIntoAColumnWithADefault_AndFiresInsertTriggers()
    {
        var rows = new MigrationRows(
            ["id", "label"],
            [
                new Dictionary<string, object?> { ["id"] = 1, ["label"] = null },
                new Dictionary<string, object?> { ["id"] = 2 }
            ]);

        await WriteAsync("events", async target => (await _writer.InsertAsync(target, rows, CancellationToken.None)).Should().Be(2));

        (await RowsAsync($"SELECT id, label FROM [{_schema}].events ORDER BY id")).Should().Equal("1|<null>", "2|<null>");
        (await RowsAsync($"SELECT id FROM [{_schema}].event_log ORDER BY id")).Should().Equal("1", "2");
    }

    // Each chunk is its own upsert inside the run's one transaction: IDENTITY_INSERT and the stage are cleaned up between them.
    [Test]
    public async Task Upsert_InTwoChunksOfOneTransaction_IntoAnIdentityKeyedTable()
    {
        await WriteAsync("people", async target =>
        {
            var first = new MigrationRows(["id", "name"], [new Dictionary<string, object?> { ["id"] = 1, ["name"] = "renamed" }]);
            var second = new MigrationRows(["id", "name"], [new Dictionary<string, object?> { ["id"] = 9, ["name"] = "ninth" }]);
            (await _writer.UpsertAsync(target, first, ["id"], CancellationToken.None)).Should().Be(1);
            (await _writer.UpsertAsync(target, second, ["id"], CancellationToken.None)).Should().Be(1);
            (await StagesAsync(target)).Should().Be(0);
        });

        (await PeopleAsync()).Should().Equal("1|renamed", "9|ninth");
    }

    // Two rows with the same key in one chunk fail the upsert. The clean-up still runs: no stage is left and IDENTITY_INSERT
    // is off again on the connection, which the run's rollback leaves unchanged.
    [Test]
    public async Task Upsert_WithADuplicateKeyInOneChunk_Fails_CleansUp_AndIsRolledBack()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        var target = new MigrationWriteTarget(connection, transaction, _schema, "people", 30);
        var rows = new MigrationRows(
            ["id", "name"],
            [
                new Dictionary<string, object?> { ["id"] = 1, ["name"] = "one" },
                new Dictionary<string, object?> { ["id"] = 1, ["name"] = "two" }
            ]);

        var upsert = () => _writer.UpsertAsync(target, rows, ["id"], CancellationToken.None);

        (await upsert.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(8672);
        (await StagesAsync(target)).Should().Be(0);
        await transaction.RollbackAsync();

        // With IDENTITY_INSERT still on, an insert without an explicit identity value would fail.
        await using var insert = new SqlCommand($"INSERT INTO [{_schema}].people (name) VALUES ('after')", connection);
        await insert.ExecuteNonQueryAsync();
        (await PeopleAsync()).Should().HaveCount(2).And.Contain("1|first");
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

        (await ScalarAsync($"SELECT count(*) FROM [{_schema}].links")).Should().Be(2);
    }

    [Test]
    public async Task Upsert_IntoAnIdentityKeyedTable_KeepsTheGivenIdentityValues()
    {
        var rows = new MigrationRows(
            ["id", "name"],
            [
                new Dictionary<string, object?> { ["id"] = 1, ["name"] = "renamed" },
                new Dictionary<string, object?> { ["id"] = 7, ["name"] = "seventh" }
            ]);

        await WriteAsync("people", async target => (await _writer.UpsertAsync(target, rows, ["id"], CancellationToken.None)).Should().Be(2));

        (await PeopleAsync()).Should().Equal("1|renamed", "7|seventh");
        (await ScalarAsync($"SELECT count(*) FROM tempdb.sys.tables WHERE name LIKE '#beacon_stage[_]%'")).Should().Be(0, "the stage is dropped");
    }

    // SQL Server compares names ignoring case; the copy matches them case-sensitively, so names are spelled as the table does.
    [Test]
    public async Task ColumnNamesInAnotherCase_WriteIntoTheTablesColumns_OnInsertAndUpsert()
    {
        var insert = new MigrationRows(
            ["TENANT", "Id", "Total", "NOTE"],
            [new Dictionary<string, object?> { ["TENANT"] = 4, ["Id"] = 1, ["Total"] = 4m, ["NOTE"] = "inserted" }]);
        var upsert = new MigrationRows(
            ["Tenant", "ID", "NOTE"],
            [new Dictionary<string, object?> { ["Tenant"] = 1, ["ID"] = 1, ["NOTE"] = "upserted" }]);

        await WriteAsync("Orders", async target =>
        {
            (await _writer.InsertAsync(target, insert, CancellationToken.None)).Should().Be(1);
            (await _writer.UpsertAsync(target, upsert, ["tenant", "id"], CancellationToken.None)).Should().Be(1);
        });

        (await OrdersAsync()).Should().Equal("1|1|10.00|upserted", "1|2|20.00|keep", "4|1|4.00|inserted");
    }

    // A failed write rolls the whole run back: the truncate is undone with it.
    [Test]
    public async Task ATruncate_IsRolledBack_WhenARowFails()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        var target = new MigrationWriteTarget(connection, transaction, _schema, "Orders", 30);

        await _writer.ClearAsync(target, CancellationToken.None);
        var failing = () => _writer.InsertAsync(target, Rows(Order(5, 1, 1m, "ok"), Order(5, 1, 2m, "duplicate key")), CancellationToken.None);
        await failing.Should().ThrowAsync<SqlException>();
        await transaction.RollbackAsync();

        (await OrdersAsync()).Should().Equal("1|1|10.00|old", "1|2|20.00|keep");
    }

    private async Task WriteAsync(string table, Func<MigrationWriteTarget, Task> write)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        await write(new MigrationWriteTarget(connection, transaction, _schema, table, 30));
        await transaction.CommitAsync();
    }

    private static MigrationRows Rows(params Dictionary<string, object?>[] rows) => new(["tenant", "id", "total", "note"], rows);

    private static Dictionary<string, object?> Order(int tenant, int id, decimal total, string note) =>
        new() { ["tenant"] = tenant, ["id"] = id, ["total"] = total, ["note"] = note };

    private Task<List<string>> OrdersAsync() => RowsAsync($"SELECT tenant, id, total, note FROM [{_schema}].[Orders] ORDER BY tenant, id");

    private Task<List<string>> PeopleAsync() => RowsAsync($"SELECT id, name FROM [{_schema}].people ORDER BY id");

    // The staging tables left in tempdb, read on the target's connection inside its transaction.
    private static async Task<object?> StagesAsync(MigrationWriteTarget target)
    {
        await using var command = new SqlCommand(
            "SELECT count(*) FROM tempdb.sys.tables WHERE name LIKE '#beacon[_]stage[_]%'",
            (SqlConnection)target.Connection,
            (SqlTransaction)target.Transaction);

        return await command.ExecuteScalarAsync();
    }

    private async Task<List<string>> RowsAsync(string sql)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
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
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);

        return await command.ExecuteScalarAsync();
    }

    private async Task ExecuteAsync(params string[] statements)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        foreach (var statement in statements)
        {
            await using var command = new SqlCommand(statement, connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
