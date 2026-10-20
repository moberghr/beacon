using Beacon.Connector.SqlServer;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The exact SQL the SQL Server migration writer sends: every name bracket-quoted, values never in the text, a MERGE that
/// updates only changed rows, IDENTITY_INSERT around it when an identity column is inserted, and no UPDATE branch when
/// nothing is left to update.
/// </summary>
[TestFixture]
public class SqlServerMigrationSqlTests
{
    [Test]
    public void Table_IsBracketQuoted_AndSchemaQualifiedWhenASchemaIsNamed()
    {
        SqlServerMigrationSql.Table(null, "Orders").Should().Be("[Orders]");
        SqlServerMigrationSql.Table("sales", "Orders").Should().Be("[sales].[Orders]");
    }

    [Test]
    public void Truncate_QuotesTheTable()
    {
        SqlServerMigrationSql.Truncate("sales", "orders").Should().Be("TRUNCATE TABLE [sales].[orders]");
    }

    [Test]
    public void DestinationColumns_TakesTheTableAsAParameter()
    {
        SqlServerMigrationSql.DestinationColumns().Should().Be(
            "SELECT c.name, c.is_identity, CAST(CASE WHEN c.system_type_id IN (34, 35, 99, 240, 241) THEN 0 ELSE 1 END AS bit) AS is_comparable "
            + "FROM sys.columns AS c WHERE c.object_id = OBJECT_ID(@table) ORDER BY c.column_id");
        SqlServerMigrationSql.TableParameter.Should().Be("@table");
    }

    [Test]
    public void NewStageName_IsAFreshPlainIdentifier_AndItsTableIsSessionTemporary()
    {
        var first = SqlServerMigrationSql.NewStageName();
        var second = SqlServerMigrationSql.NewStageName();

        first.Should().MatchRegex("^beacon_stage_[0-9a-f]{32}$");
        second.Should().NotBe(first);
        SqlServerMigrationSql.StageTable("beacon_stage_1").Should().Be("[#beacon_stage_1]");
    }

    [Test]
    public void CreateStage_CopiesTheShapeOfTheWrittenColumns_WithoutRows()
    {
        var sql = SqlServerMigrationSql.CreateStage("beacon_stage_1", "sales", "orders", ["id", "Note"]);

        sql.Should().Be("SELECT TOP 0 [id], [Note] INTO [#beacon_stage_1] FROM [sales].[orders]");
    }

    [Test]
    public void MergeFromStage_UpdatesOnlyChangedRows_AndInsertsNewKeys()
    {
        var sql = SqlServerMigrationSql.MergeFromStage("beacon_stage_1", null, "orders", ["tenant", "id", "total", "Note"], ["tenant", "id"], [], []);

        sql.Should().Be(
            "MERGE [orders] WITH (HOLDLOCK) AS target USING [#beacon_stage_1] AS source "
            + "ON target.[tenant] = source.[tenant] AND target.[id] = source.[id] "
            + "WHEN MATCHED AND EXISTS (SELECT source.[total], source.[Note] EXCEPT SELECT target.[total], target.[Note]) "
            + "THEN UPDATE SET target.[total] = source.[total], target.[Note] = source.[Note] "
            + "WHEN NOT MATCHED THEN INSERT ([tenant], [id], [total], [Note]) VALUES (source.[tenant], source.[id], source.[total], source.[Note]);");
    }

    // xml, text, geography and the like cannot take part in EXCEPT: every matched row is updated instead.
    [Test]
    public void MergeFromStage_WhenAnUpdatableColumnCannotBeCompared_UpdatesEveryMatchedRow()
    {
        var sql = SqlServerMigrationSql.MergeFromStage("beacon_stage_1", null, "docs", ["id", "body", "title"], ["id"], [], ["Body"]);

        sql.Should().Be(
            "MERGE [docs] WITH (HOLDLOCK) AS target USING [#beacon_stage_1] AS source ON target.[id] = source.[id] "
            + "WHEN MATCHED THEN UPDATE SET target.[body] = source.[body], target.[title] = source.[title] "
            + "WHEN NOT MATCHED THEN INSERT ([id], [body], [title]) VALUES (source.[id], source.[body], source.[title]);");
    }

    // A column the upsert does not update (here one it does not write) keeps the changed-only check.
    [Test]
    public void MergeFromStage_WhenOnlyAColumnNotUpdatedCannotBeCompared_KeepsTheChangedOnlyCheck()
    {
        var sql = SqlServerMigrationSql.MergeFromStage("beacon_stage_1", null, "docs", ["id", "title"], ["id"], [], ["body"]);

        sql.Should().Contain("WHEN MATCHED AND EXISTS (SELECT source.[title] EXCEPT SELECT target.[title]) THEN UPDATE");
    }

    [Test]
    public void MergeFromStage_OnAnAllKeyTable_HasNoUpdateBranch()
    {
        var sql = SqlServerMigrationSql.MergeFromStage("beacon_stage_1", "sales", "links", ["a", "b"], ["a", "b"], [], []);

        sql.Should().Be(
            "MERGE [sales].[links] WITH (HOLDLOCK) AS target USING [#beacon_stage_1] AS source "
            + "ON target.[a] = source.[a] AND target.[b] = source.[b] "
            + "WHEN NOT MATCHED THEN INSERT ([a], [b]) VALUES (source.[a], source.[b]);");
    }

    [Test]
    public void MergeFromStage_InsertingAnIdentityColumn_RunsWithIdentityInsert_AndNeverUpdatesIt()
    {
        // Key and identity columns are matched case-insensitively, the way SQL Server compares names.
        var sql = SqlServerMigrationSql.MergeFromStage("beacon_stage_1", "sales", "orders", ["ID", "seq", "total"], ["id"], ["Seq", "id"], []);

        sql.Should().Be(
            "SET IDENTITY_INSERT [sales].[orders] ON; "
            + "MERGE [sales].[orders] WITH (HOLDLOCK) AS target USING [#beacon_stage_1] AS source ON target.[id] = source.[id] "
            + "WHEN MATCHED AND EXISTS (SELECT source.[total] EXCEPT SELECT target.[total]) THEN UPDATE SET target.[total] = source.[total] "
            + "WHEN NOT MATCHED THEN INSERT ([ID], [seq], [total]) VALUES (source.[ID], source.[seq], source.[total]); "
            + "SET IDENTITY_INSERT [sales].[orders] OFF;");
    }

    [Test]
    public void InsertsIdentity_OnlyWhenAWrittenColumnIsAnIdentityColumn()
    {
        SqlServerMigrationSql.InsertsIdentity(["id", "total"], ["ID"]).Should().BeTrue();
        SqlServerMigrationSql.InsertsIdentity(["total"], ["id"]).Should().BeFalse();
        SqlServerMigrationSql.InsertsIdentity(["total"], []).Should().BeFalse();
    }

    [Test]
    public void IdentityInsert_And_DropStage_QuoteTheirTables()
    {
        SqlServerMigrationSql.IdentityInsert(null, "orders", false).Should().Be("SET IDENTITY_INSERT [orders] OFF;");
        SqlServerMigrationSql.DropStage("beacon_stage_1").Should().Be("DROP TABLE IF EXISTS [#beacon_stage_1]");
    }

    [TestCase("bad name")]
    [TestCase("x]y")]
    [TestCase("x;DROP")]
    [TestCase("")]
    public void ANameThatIsNotAPlainIdentifier_IsRefused(string name)
    {
        var table = () => SqlServerMigrationSql.Table(null, name);
        var column = () => SqlServerMigrationSql.CreateStage("beacon_stage_1", null, "t", [name]);
        var stage = () => SqlServerMigrationSql.StageTable(name);

        table.Should().Throw<InvalidOperationException>();
        column.Should().Throw<InvalidOperationException>();
        stage.Should().Throw<InvalidOperationException>();
    }
}
