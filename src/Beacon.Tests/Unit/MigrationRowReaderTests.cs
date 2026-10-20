using Beacon.Connector.SqlServer;
using Beacon.Core.DataMigration;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The reader SqlBulkCopy streams migration rows from: one field per column in column order, names resolved exactly then
/// ignoring case, and a NULL or a missing value read as DBNull (never a stale or shifted value).
/// </summary>
[TestFixture]
public class MigrationRowReaderTests
{
    private static readonly MigrationRows Rows = new(
        ["id", "Name", "note"],
        [
            new Dictionary<string, object?> { ["id"] = 1, ["Name"] = "a", ["note"] = "first" },
            new Dictionary<string, object?> { ["id"] = 2, ["Name"] = null },
            new Dictionary<string, object?> { ["note"] = "third", ["id"] = 3, ["Name"] = "c" }
        ]);

    [Test]
    public void Fields_AreTheColumns_InOrder()
    {
        using var reader = new MigrationRowReader(Rows);

        reader.FieldCount.Should().Be(3);
        Enumerable.Range(0, reader.FieldCount)
            .Select(x => reader.GetName(x))
            .Should().Equal("id", "Name", "note");
        reader.HasRows.Should().BeTrue();
    }

    [Test]
    public void GetOrdinal_MatchesExactlyFirst_ThenIgnoringCase()
    {
        using var reader = new MigrationRowReader(new MigrationRows(["Id", "id", "Total"], []));

        reader.GetOrdinal("id").Should().Be(1);
        reader.GetOrdinal("Id").Should().Be(0);
        reader.GetOrdinal("ID").Should().Be(0);
        reader.GetOrdinal("total").Should().Be(2);
        var unknown = () => reader.GetOrdinal("missing");
        unknown.Should().Throw<IndexOutOfRangeException>();
    }

    [Test]
    public void Read_VisitsEveryRow_ThenStops()
    {
        using var reader = new MigrationRowReader(Rows);
        var ids = new List<object>();

        while (reader.Read())
        {
            ids.Add(reader.GetValue(0));
        }

        ids.Should().Equal(1, 2, 3);
        reader.Read().Should().BeFalse();
        reader.NextResult().Should().BeFalse();
    }

    [Test]
    public void ANullAndAMissingValue_ReadAsDBNull()
    {
        using var reader = new MigrationRowReader(Rows);
        reader.Read();
        reader.Read();

        reader.GetValue(1).Should().Be(DBNull.Value);
        reader.IsDBNull(1).Should().BeTrue();
        reader.GetValue(2).Should().Be(DBNull.Value, "the row has no 'note'");
        reader.IsDBNull(2).Should().BeTrue();
        reader["id"].Should().Be(2);
    }

    [Test]
    public void ValuesFollowTheColumnOrder_NotTheRowsKeyOrder()
    {
        using var reader = new MigrationRowReader(Rows);
        reader.Read();
        reader.Read();
        reader.Read();
        var values = new object[3];

        reader.GetValues(values).Should().Be(3);

        values.Should().Equal(3, "c", "third");
        reader.GetInt32(0).Should().Be(3);
        reader.GetString(1).Should().Be("c");
    }

    [Test]
    public void GetFieldType_IsTheTypeOfTheColumnsFirstValue_OrObjectWhenAllAreNull()
    {
        using var reader = new MigrationRowReader(new MigrationRows(
            ["id", "empty"],
            [
                new Dictionary<string, object?> { ["id"] = null },
                new Dictionary<string, object?> { ["id"] = 5L, ["empty"] = null }
            ]));

        reader.GetFieldType(0).Should().Be(typeof(long));
        reader.GetFieldType(1).Should().Be(typeof(object));
    }

    [Test]
    public void GetBytes_ReadsABinaryValue_InPieces()
    {
        using var reader = new MigrationRowReader(new MigrationRows(
            ["blob"],
            [new Dictionary<string, object?> { ["blob"] = new byte[] { 1, 2, 3, 4, 5 } }]));
        reader.Read();
        var buffer = new byte[3];

        reader.GetBytes(0, 0, null, 0, 0).Should().Be(5);
        reader.GetBytes(0, 3, buffer, 0, 3).Should().Be(2);
        buffer.Take(2).Should().Equal(4, 5);
    }

    [Test]
    public void AValue_BeforeTheFirstRead_IsRefused()
    {
        using var reader = new MigrationRowReader(Rows);

        var act = () => reader.GetValue(0);

        act.Should().Throw<InvalidOperationException>();
    }
}
