using System.Globalization;
using System.Text.Json;
using Beacon.Connector.MySql;
using Beacon.Core.DataMigration;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The commands the MySQL migration writer sends, on a recording connection (no database, §4.7): every name backtick-quoted,
/// every value a parameter, every command in the run's transaction with its timeout, a command closed at each of its three
/// limits (rows, parameters, payload) without splitting a row, TRUNCATE then START TRANSACTION to
/// clear, and the rows processed counted.
/// </summary>
[TestFixture]
public class MySqlMigrationBulkWriterTests
{
    private const int Timeout = 77;

    private RecordingDbConnection _connection = null!;
    private MySqlMigrationBulkWriter _writer = null!;
    private MigrationWriteTarget _target = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = new RecordingDbConnection();
        _connection.Open();
        _writer = new MySqlMigrationBulkWriter(NullLogger<MySqlMigrationBulkWriter>.Instance);
        _target = new MigrationWriteTarget(_connection, _connection.BeginTransaction(), "sales", "Orders", Timeout);
    }

    [TearDown]
    public void TearDown()
    {
        _target.Transaction.Dispose();
        _connection.Dispose();
    }

    [Test]
    public async Task Insert_SendsOneMultiRowInsert_WithEveryValueAParameter_InTheRunsTransaction()
    {
        var rows = new MigrationRows(["id", "Note"], [Row(("id", 1), ("Note", "a")), Row(("id", 2), ("Note", "b"))]);

        var written = await _writer.InsertAsync(_target, rows, CancellationToken.None);

        written.Should().Be(2);
        var command = _connection.Executions.Single();
        command.CommandText.Should().Be("INSERT INTO `sales`.`Orders` (`id`, `Note`) VALUES (@p0, @p1), (@p2, @p3)");
        command.Transaction.Should().BeSameAs(_target.Transaction);
        command.Parameters.Select(x => (x.ParameterName, x.Value)).Should().Equal(("@p0", 1), ("@p1", "a"), ("@p2", 2), ("@p3", "b"));
        _connection.CommandTimeouts.Should().Equal(Timeout);
    }

    [Test]
    public async Task Insert_IntoATableWithoutASchema_NamesTheTableAlone()
    {
        var target = _target with { Schema = null };

        await _writer.InsertAsync(target, new MigrationRows(["id"], [Row(("id", 1))]), CancellationToken.None);

        _connection.ExecutedCommands.Single().Should().Be("INSERT INTO `Orders` (`id`) VALUES (@p0)");
    }

    [Test]
    public async Task ANullOrAMissingValue_IsBoundAsDbNull()
    {
        var rows = new MigrationRows(["id", "note"], [Row(("id", 1), ("note", null)), Row(("id", 2))]);

        await _writer.InsertAsync(_target, rows, CancellationToken.None);

        _connection.Executions.Single().Parameters.Select(x => x.Value).Should().Equal(1, DBNull.Value, 2, DBNull.Value);
    }

    [Test]
    public async Task Upsert_UpdatesEveryNonKeyColumn_OnADuplicateKey_AndCountsTheRowsProcessed()
    {
        var rows = new MigrationRows(
            ["tenant", "id", "total", "Note"],
            [Row(("tenant", 1), ("id", 1), ("total", 5m), ("Note", "x"))]);

        var written = await _writer.UpsertAsync(_target, rows, ["tenant", "id"], CancellationToken.None);

        written.Should().Be(1);
        var command = _connection.Executions.Single();
        command.CommandText.Should().Be(
            "INSERT INTO `sales`.`Orders` (`tenant`, `id`, `total`, `Note`) VALUES (@p0, @p1, @p2, @p3) "
            + "ON DUPLICATE KEY UPDATE `total` = VALUES(`total`), `Note` = VALUES(`Note`)");
        command.Transaction.Should().BeSameAs(_target.Transaction);
        command.Parameters.Should().HaveCount(4);
        _connection.CommandTimeouts.Should().Equal(Timeout);
    }

    // Nothing to update: the key is assigned to itself, so an existing row stays and any other error still fails the statement.
    [Test]
    public async Task Upsert_IntoAnAllKeyTable_LeavesExistingRowsAlone_WithoutIgnoringErrors()
    {
        var rows = new MigrationRows(["a", "b"], [Row(("a", 1), ("b", 2))]);

        await _writer.UpsertAsync(_target with { Schema = null, Table = "links" }, rows, ["a", "b"], CancellationToken.None);

        var sql = _connection.ExecutedCommands.Single();
        sql.Should().Be("INSERT INTO `links` (`a`, `b`) VALUES (@p0, @p1) ON DUPLICATE KEY UPDATE `a` = `a`");
        sql.Should().NotContain("IGNORE");
    }

    [Test]
    public async Task Upsert_WithoutKeyColumns_Throws_BeforeAnyCommand()
    {
        var upsert = () => _writer.UpsertAsync(_target, new MigrationRows(["id"], [Row(("id", 1))]), [], CancellationToken.None);

        await upsert.Should().ThrowAsync<InvalidOperationException>().WithMessage("*primary key*");
        _connection.Executions.Should().BeEmpty();
    }

    // The server refuses TRUNCATE of a table another table references; TRUNCATE commits implicitly, so the transaction is
    // started again right after it for the rows that follow.
    [Test]
    public async Task Clear_TruncatesTheTable_ThenStartsATransactionAgain_InTheRunsTransaction()
    {
        await _writer.ClearAsync(_target, CancellationToken.None);

        _connection.ExecutedCommands.Should().Equal("TRUNCATE TABLE `sales`.`Orders`", "START TRANSACTION");
        _connection.Executions.Should().AllSatisfy(x => x.Parameters.Should().BeEmpty());
        EveryCommandRunsInTheTransactionWithTheTimeout();
    }

    [Test]
    public async Task Clear_WithoutASchema_TruncatesTheTableInTheConnectionsDatabase()
    {
        await _writer.ClearAsync(_target with { Schema = null }, CancellationToken.None);

        _connection.ExecutedCommands.Should().Equal("TRUNCATE TABLE `Orders`", "START TRANSACTION");
    }

    [Test]
    public async Task ACommand_ClosesAtTheRowLimit()
    {
        var rows = new MigrationRows(["id"], Enumerable.Range(0, MySqlMigrationSql.MaxRowsPerCommand + 1).Select(x => Row(("id", x))).ToList());

        var written = await _writer.InsertAsync(_target, rows, CancellationToken.None);

        written.Should().Be(MySqlMigrationSql.MaxRowsPerCommand + 1);
        RowsPerCommand(1).Should().Equal(MySqlMigrationSql.MaxRowsPerCommand, 1);
        EveryCommandRunsInTheTransactionWithTheTimeout();
        _connection.Executions[1].Parameters.Single().ParameterName.Should().Be("@p0", "each command numbers its parameters from 0");
        _connection.Executions[1].Parameters.Single().Value.Should().Be(MySqlMigrationSql.MaxRowsPerCommand);
    }

    [Test]
    public async Task ACommand_ClosesAtTheParameterLimit()
    {
        const int columnCount = 100;
        var columns = Enumerable.Range(0, columnCount).Select(x => $"c{x}").ToList();
        var rowsPerCommand = MySqlMigrationSql.MaxParametersPerCommand / columnCount;
        var rows = new MigrationRows(
            columns,
            Enumerable.Range(0, rowsPerCommand + 1)
                .Select(x => (IReadOnlyDictionary<string, object?>)columns.ToDictionary(y => y, y => y == "c0" ? (object?)x : null))
                .ToList());

        var written = await _writer.UpsertAsync(_target, rows, ["c0"], CancellationToken.None);

        written.Should().Be(rowsPerCommand + 1);
        RowsPerCommand(columnCount).Should().Equal(rowsPerCommand, 1);
        _connection.Executions[0].Parameters.Should().HaveCount(MySqlMigrationSql.MaxParametersPerCommand);
        _connection.ExecutedCommands.Should().OnlyContain(x => x.Contains("ON DUPLICATE KEY UPDATE", StringComparison.Ordinal));
        EveryCommandRunsInTheTransactionWithTheTimeout();
    }

    [Test]
    public async Task ACommand_ClosesAtThePayloadLimit()
    {
        var text = new string('x', 300_000);
        var rows = new MigrationRows(["id", "body"], Enumerable.Range(0, 4).Select(x => Row(("id", x), ("body", text))).ToList());

        var written = await _writer.InsertAsync(_target, rows, CancellationToken.None);

        written.Should().Be(4);
        RowsPerCommand(2).Should().Equal(3, 1);
        EveryCommandRunsInTheTransactionWithTheTimeout();
    }

    // A row is never split: one larger than the payload limit goes alone, between the rows around it.
    [Test]
    public async Task ARowAboveThePayloadLimit_GoesInACommandOfItsOwn()
    {
        var large = new string('y', MySqlMigrationSql.MaxPayloadBytesPerCommand * 2);
        var rows = new MigrationRows(["id", "body"], [Row(("id", 1), ("body", "a")), Row(("id", 2), ("body", large)), Row(("id", 3), ("body", "c"))]);

        var written = await _writer.InsertAsync(_target, rows, CancellationToken.None);

        written.Should().Be(3);
        RowsPerCommand(2).Should().Equal(1, 1, 1);
        _connection.Executions[1].Parameters.Select(x => x.Value).Should().Equal(2, large);
    }

    [Test]
    public void EstimatedSize_CountsUtf8Bytes_ByteArrays_AndAFixedSizeForOtherValues()
    {
        MySqlMigrationSql.EstimatedSize("čž").Should().Be(4);
        MySqlMigrationSql.EstimatedSize(new byte[10]).Should().Be(10);
        MySqlMigrationSql.EstimatedSize(null).Should().Be(0);
        MySqlMigrationSql.EstimatedSize(DBNull.Value).Should().Be(0);
        MySqlMigrationSql.EstimatedSize(12.5m).Should().Be(40);
        MySqlMigrationSql.EstimatedSize(Guid.NewGuid()).Should().Be(40);
        MySqlMigrationSql.EstimatedSize(DateTime.UtcNow).Should().Be(40);
        Guid.NewGuid().ToString().Length.Should().BeLessThanOrEqualTo(MySqlMigrationSql.FixedValueSize, "a GUID goes as 36 characters of text");
        decimal.MinValue.ToString(CultureInfo.InvariantCulture).Length.Should().BeLessThanOrEqualTo(MySqlMigrationSql.FixedValueSize);
    }

    // MySql.Data formats a DateOnly with the current culture, drops a TimeOnly's seconds and cannot bind a JsonElement.
    [Test]
    public void Bindable_ConvertsWhatTheDriverBindsWrongly()
    {
        MySqlMigrationBulkWriter.Bindable(new DateOnly(2026, 1, 2)).Should().Be(new DateTime(2026, 1, 2));
        MySqlMigrationBulkWriter.Bindable(new TimeOnly(13, 14, 15, 16)).Should().Be(new TimeSpan(0, 13, 14, 15, 16));
        MySqlMigrationBulkWriter.Bindable(Json("""{"a": [1, "x"]}""")).Should().Be("""{"a": [1, "x"]}""");
        MySqlMigrationBulkWriter.Bindable(Json("[1,2]")).Should().Be("[1,2]");
        MySqlMigrationBulkWriter.Bindable(Json("\"héllo\"")).Should().Be("héllo");
        MySqlMigrationBulkWriter.Bindable(Json("12.50")).Should().Be("12.50");
        MySqlMigrationBulkWriter.Bindable(Json("true")).Should().Be(true);
        MySqlMigrationBulkWriter.Bindable(Json("false")).Should().Be(false);
        MySqlMigrationBulkWriter.Bindable(Json("null")).Should().Be(DBNull.Value);
        MySqlMigrationBulkWriter.Bindable(default(JsonElement)).Should().Be(DBNull.Value);
        MySqlMigrationBulkWriter.Bindable(null).Should().Be(DBNull.Value);
    }

    [Test]
    public void Bindable_LeavesWhatTheDriverBindsCorrectly()
    {
        var guid = Guid.NewGuid();
        var bytes = new byte[] { 1, 2 };
        var offset = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2));
        object[] values = [new DateTime(2026, 1, 2), offset, guid, true, bytes, TimeSpan.FromHours(26), ulong.MaxValue, long.MinValue, 1.5d, 2.5m, "čž"];

        values.Select(MySqlMigrationBulkWriter.Bindable).Should().Equal(values);
    }

    // The JSON text, not a fixed size, counts towards a command's payload.
    [Test]
    public async Task AJsonValue_CountsItsTextTowardsThePayload_AndIsBoundAsText()
    {
        var json = Json($$"""{"body": "{{new string('x', 600_000)}}"}""");
        var rows = new MigrationRows(["id", "doc"], [Row(("id", 1), ("doc", json)), Row(("id", 2), ("doc", json))]);

        await _writer.InsertAsync(_target, rows, CancellationToken.None);

        RowsPerCommand(2).Should().Equal(1, 1);
        _connection.Executions[0].Parameters[1].Value.Should().Be(json.GetRawText());
    }

    [Test]
    public async Task ATransactionOfAnotherConnection_IsRefused()
    {
        using var other = new RecordingDbConnection();
        other.Open();
        using var otherTransaction = other.BeginTransaction();

        var insert = () => _writer.InsertAsync(_target with { Transaction = otherTransaction }, new MigrationRows(["id"], [Row(("id", 1))]), CancellationToken.None);

        await insert.Should().ThrowAsync<InvalidOperationException>();
        _connection.Executions.Should().BeEmpty();
        other.Executions.Should().BeEmpty();
    }

    [TestCase("bad name")]
    [TestCase("x`y")]
    public void ANameThatIsNotAPlainIdentifier_IsRefused(string name)
    {
        var insert = () => MySqlMigrationSql.Insert(null, "orders", [name], 1);
        var table = () => MySqlMigrationSql.Table(name, "orders");

        insert.Should().Throw<InvalidOperationException>();
        table.Should().Throw<InvalidOperationException>();
    }

    private List<int> RowsPerCommand(int columnCount)
    {
        return _connection.Executions
            .Select(x => x.Parameters.Count / columnCount)
            .ToList();
    }

    private void EveryCommandRunsInTheTransactionWithTheTimeout()
    {
        _connection.Executions.Should().OnlyContain(x => x.Transaction == _target.Transaction);
        _connection.CommandTimeouts.Should().HaveCount(_connection.Executions.Count).And.OnlyContain(x => x == Timeout);
    }

    private static JsonElement Json(string text)
    {
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static IReadOnlyDictionary<string, object?> Row(params (string Column, object? Value)[] values)
    {
        return values.ToDictionary(x => x.Column, x => x.Value);
    }
}
