using Beacon.Connector.PostgreSql;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The exact SQL the PostgreSQL migration writer sends: every name double-quoted (so it matches as entered), values never in
/// the text, and an all-key table upserted with DO NOTHING.
/// </summary>
[TestFixture]
public class PostgreSqlMigrationSqlTests
{
    [Test]
    public void Table_IsQuoted_AndSchemaQualifiedWhenASchemaIsNamed()
    {
        PostgreSqlMigrationSql.Table(null, "Orders").Should().Be("\"Orders\"");
        PostgreSqlMigrationSql.Table("sales", "Orders").Should().Be("\"sales\".\"Orders\"");
    }

    [Test]
    public void Truncate_QuotesTheTable()
    {
        PostgreSqlMigrationSql.Truncate("sales", "orders").Should().Be("TRUNCATE TABLE \"sales\".\"orders\"");
    }

    [Test]
    public void CopyInto_ListsTheQuotedColumns_InOrder()
    {
        var sql = PostgreSqlMigrationSql.CopyInto(PostgreSqlMigrationSql.Table(null, "orders"), ["id", "CustomerName"]);

        sql.Should().Be("COPY \"orders\" (\"id\", \"CustomerName\") FROM STDIN (FORMAT BINARY)");
    }

    [Test]
    public void NewStageName_IsAFreshLowerCaseIdentifier()
    {
        var first = PostgreSqlMigrationSql.NewStageName();
        var second = PostgreSqlMigrationSql.NewStageName();

        first.Should().MatchRegex("^beacon_stage_[0-9a-f]{32}$");
        second.Should().NotBe(first);
    }

    // Only the written columns and their types: no NOT NULL, defaults or sequences come along.
    [Test]
    public void CreateStage_IsAnEmptyTemporaryTableOfTheWrittenColumns_DroppedOnCommit()
    {
        var sql = PostgreSqlMigrationSql.CreateStage("beacon_stage_1", "sales", "orders", ["id", "Note"]);

        sql.Should().Be("CREATE TEMP TABLE \"beacon_stage_1\" ON COMMIT DROP AS SELECT \"id\", \"Note\" FROM \"sales\".\"orders\" WITH NO DATA");
    }

    [Test]
    public void DropStage_QuotesTheStage()
    {
        PostgreSqlMigrationSql.DropStage("beacon_stage_1").Should().Be("DROP TABLE \"beacon_stage_1\"");
    }

    [Test]
    public void MergeFromStage_UpdatesEveryNonKeyColumn_OnAKeyConflict()
    {
        var sql = PostgreSqlMigrationSql.MergeFromStage("beacon_stage_1", null, "orders", ["tenant", "id", "total", "Note"], ["tenant", "id"]);

        sql.Should().Be(
            "INSERT INTO \"orders\" (\"tenant\", \"id\", \"total\", \"Note\") SELECT \"tenant\", \"id\", \"total\", \"Note\" FROM \"beacon_stage_1\" "
            + "ON CONFLICT (\"tenant\", \"id\") DO UPDATE SET \"total\" = EXCLUDED.\"total\", \"Note\" = EXCLUDED.\"Note\"");
    }

    [Test]
    public void MergeFromStage_OnAnAllKeyTable_LeavesExistingRowsAlone()
    {
        var sql = PostgreSqlMigrationSql.MergeFromStage("beacon_stage_1", "sales", "links", ["a", "b"], ["a", "b"]);

        sql.Should().Be(
            "INSERT INTO \"sales\".\"links\" (\"a\", \"b\") SELECT \"a\", \"b\" FROM \"beacon_stage_1\" ON CONFLICT (\"a\", \"b\") DO NOTHING");
    }

    [TestCase("bad name")]
    [TestCase("x\"y")]
    [TestCase("")]
    public void ANameThatIsNotAPlainIdentifier_IsRefused(string name)
    {
        var table = () => PostgreSqlMigrationSql.Table(null, name);
        var column = () => PostgreSqlMigrationSql.CopyInto("\"t\"", [name]);

        table.Should().Throw<InvalidOperationException>();
        column.Should().Throw<InvalidOperationException>();
    }
}
