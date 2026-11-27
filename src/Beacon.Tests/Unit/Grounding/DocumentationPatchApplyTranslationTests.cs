using System.Text.RegularExpressions;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.McpLearning;
using Beacon.Core.Models;
using Beacon.Core.SecurityAudit;
using Beacon.Tests.Common;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Grounding;

/// <summary>
/// Applying a column or table description patch, as PostgreSQL and SQL Server receive it, captured without a database
/// (§4.7): the patch and the target are read, then the description is written in one statement that also compares it with
/// the content captured when the patch was proposed (<c>IS NULL</c> when none was), then the patch is marked applied in one
/// statement conditional on it still being proposed. A description write that changes nothing (another write landed
/// between the read and the write) refuses the patch: the patch is not marked applied and no event is recorded. After the
/// status, the table's columns are read and the description item is replaced under the catalog's names, or added when there
/// is none.
/// </summary>
[TestFixture]
public class DocumentationPatchApplyTranslationTests
{
    private const string Captured = "The order's state.";
    private const string Proposed = "Where the order is in fulfilment.";

    // SHA-256 of the structure TableColumns gives: "2:id7:integer6:status4:text".
    private const string OrdersHash = "b64cea7567e48b5830903987d4d1c1b765c14be573efdda112357b6cdeb34a54";

    // The target table's columns as the apply reads them: name, data type, ordinal position.
    private static readonly object[][] TableColumns = [["status", "text", 2], ["id", "integer", 1]];

    [TestCase(false, McpDocPatchTarget.ColumnDescription, "UPDATE beacon.column_metadata AS c", "c.id = @", "c.description = @", "SET description = @")]
    [TestCase(true, McpDocPatchTarget.ColumnDescription, "UPDATE [c]", "[c].[Id] = @", "[c].[Description] = @", "SET [c].[Description] = @")]
    [TestCase(false, McpDocPatchTarget.TableDescription, "UPDATE beacon.database_metadata AS d", "d.id = @", "d.table_description = @", "SET table_description = @")]
    [TestCase(true, McpDocPatchTarget.TableDescription, "UPDATE [d]", "[d].[Id] = @", "[d].[TableDescription] = @", "SET [d].[TableDescription] = @")]
    public async Task ADescriptionPatch_IsWrittenOnlyWhileTheTargetHoldsTheCapturedContent_Translates(
        bool sqlServer, McpDocPatchTarget target, string update, string byId, string compared, string written)
    {
        var capture = Capture(sqlServer)
            .ThenRow(PatchRow(target, Captured))
            .ThenRow(TargetRow(target, Captured))
            .ThenRowsAffected(1)
            .ThenRowsAffected(1)
            .ThenRows(TableColumns)
            .ThenRowsAffected(1);
        var audit = new RecordingSecurityAudit();

        await Apply(capture, audit);

        capture.Commands.Should().HaveCount(6, "the patch, the target, the conditional description write, the patch's status, then the table's columns and its description item");
        var write = capture.Commands[2];
        write.Should().Contain(update).And.Contain(written);
        WhereClauseOf(write).Should().Contain(byId).And.Contain(compared);
        ComparedWith(capture, 2, compared[..^4]).Should().Be(Captured, "the content captured when the patch was proposed");
        capture.CommandParameters[2].Values.Should().Contain(Proposed);
        WhereClauseOf(capture.Commands[3]).Should().Contain(sqlServer ? "[m].[Status] = 0" : "m.status = 0", "only a patch still proposed is marked applied");
        audit.Events.Select(x => x.Action).Should().Equal(SecurityAuditActions.DocumentationPatchApplied);
    }

    // A patch proposed on an absent description is written only while it is still absent.
    [TestCase(false, McpDocPatchTarget.ColumnDescription, "c.description IS NULL")]
    [TestCase(true, McpDocPatchTarget.ColumnDescription, "[c].[Description] IS NULL")]
    [TestCase(false, McpDocPatchTarget.TableDescription, "d.table_description IS NULL")]
    [TestCase(true, McpDocPatchTarget.TableDescription, "[d].[TableDescription] IS NULL")]
    public async Task ADescriptionPatch_ProposedOnNoDescription_IsWrittenOnlyWhileThereIsNone_Translates(bool sqlServer, McpDocPatchTarget target, string absent)
    {
        var capture = Capture(sqlServer)
            .ThenRow(PatchRow(target, DBNull.Value))
            .ThenRow(TargetRow(target, DBNull.Value))
            .ThenRowsAffected(1)
            .ThenRowsAffected(1)
            .ThenRows(TableColumns)
            .ThenRowsAffected(1);

        await Apply(capture, new RecordingSecurityAudit());

        WhereClauseOf(capture.Commands[2]).Should().Contain(absent);
    }

    // Another write between the read and the conditional write: the write changes nothing, the patch stays proposed and
    // nothing is recorded.
    [TestCase(false, McpDocPatchTarget.ColumnDescription)]
    [TestCase(true, McpDocPatchTarget.ColumnDescription)]
    [TestCase(false, McpDocPatchTarget.TableDescription)]
    [TestCase(true, McpDocPatchTarget.TableDescription)]
    public async Task ADescriptionWriteThatChangesNothing_RefusesThePatch_Translates(bool sqlServer, McpDocPatchTarget target)
    {
        var capture = Capture(sqlServer)
            .ThenRow(PatchRow(target, Captured))
            .ThenRow(TargetRow(target, Captured))
            .ThenRowsAffected(0);
        var audit = new RecordingSecurityAudit();

        var act = () => Apply(capture, audit);

        (await act.Should().ThrowAsync<BeaconConflictException>()).Which.Message.Should().Be(ApplyDocumentationPatchHandler.TargetChangedMessage);
        capture.Commands.Should().HaveCount(3, "neither the patch's status nor a description item is written");
        audit.Events.Should().BeEmpty();
    }

    // The target read holds other content than the patch was proposed on: refused before any write.
    [TestCase(false)]
    [TestCase(true)]
    public async Task ATargetHoldingOtherContent_IsRefusedWithoutAWrite_Translates(bool sqlServer)
    {
        var capture = Capture(sqlServer)
            .ThenRow(PatchRow(McpDocPatchTarget.ColumnDescription, Captured))
            .ThenRow(TargetRow(McpDocPatchTarget.ColumnDescription, "the order's state."));

        var act = () => Apply(capture, new RecordingSecurityAudit());

        await act.Should().ThrowAsync<BeaconConflictException>();
        capture.Commands.Should().HaveCount(2, "the patch and the target are read; nothing is written");
    }

    // The item is recorded under the names the catalog holds (read with the target, here cased otherwise than the patch's
    // identifier), with the structure of the target's table read inside the apply.
    [TestCase(false, McpDocPatchTarget.ColumnDescription, "Status", "m.", "column_metadata", "mcp_description_items")]
    [TestCase(true, McpDocPatchTarget.ColumnDescription, "Status", "[m].", "[ColumnMetadata]", "[McpDescriptionItems]")]
    [TestCase(false, McpDocPatchTarget.TableDescription, "", "m.", "column_metadata", "mcp_description_items")]
    [TestCase(true, McpDocPatchTarget.TableDescription, "", "[m].", "[ColumnMetadata]", "[McpDescriptionItems]")]
    public async Task TheDescriptionItem_IsReplacedUnderTheCatalogsNames_Translates(
        bool sqlServer, McpDocPatchTarget target, string column, string alias, string columns, string items)
    {
        var capture = Capture(sqlServer)
            .ThenRow(PatchRow(target, Captured))
            .ThenRow(TargetRow(target, Captured, "Public", "Orders", "Status"))
            .ThenRowsAffected(1)
            .ThenRowsAffected(1)
            .ThenRows(TableColumns)
            .ThenRowsAffected(1);

        await Apply(capture, new RecordingSecurityAudit());

        capture.Commands[4].Should().Contain(columns);
        capture.CommandParameters[4].Values.Should().Contain(3, "the columns of the target's table");
        capture.Commands[5].Should().Contain("UPDATE").And.Contain(items);
        var names = sqlServer
            ? new[] { "[DataSourceId]", "[SchemaName]", "[TableName]", "[ColumnName]" }
            : ["data_source_id", "schema_name", "table_name", "column_name"];
        ComparedWith(capture, 5, alias + names[0]).Should().Be(10);
        ComparedWith(capture, 5, alias + names[1]).Should().Be("Public");
        ComparedWith(capture, 5, alias + names[2]).Should().Be("Orders");
        ComparedWith(capture, 5, alias + names[3]).Should().Be(column);
        capture.CommandParameters[5].Values.Should().Contain(OrdersHash).And.Contain(42).And.Contain(31);
    }

    // No item yet: one is added in the apply's unit of work.
    [Test]
    public async Task ADescriptionWithoutAnItem_AddsOne_Translates()
    {
        var capture = SqlCapture.OnSqlServer()
            .ThenRow(PatchRow(McpDocPatchTarget.ColumnDescription, Captured))
            .ThenRow(TargetRow(McpDocPatchTarget.ColumnDescription, Captured))
            .ThenRowsAffected(1)
            .ThenRowsAffected(1)
            .ThenRows(TableColumns)
            .ThenRowsAffected(0)
            .ThenRow(77);

        await Apply(capture, new RecordingSecurityAudit());

        capture.Commands.Should().HaveCount(7, "the item's update found none, so it is inserted");
        capture.Commands[6].Should().Contain("INSERT INTO [beacon].[McpDescriptionItems]");
        capture.CommandParameters[6].Values.Should().Contain(["public", "orders", "status", OrdersHash, 10, 31, 42]);
        capture.CommandParameters[6].Values.Should().Contain((int)McpDescriptionSource.Curated, "a curated description");
    }

    // Another apply added the same item between this apply's update and its insert: the apply is refused as a target change.
    [Test]
    public async Task AnItemAddedMeanwhile_RefusesTheApply_Translates()
    {
        var capture = new SqlCapture()
            .ThenRow(PatchRow(McpDocPatchTarget.ColumnDescription, Captured))
            .ThenRow(TargetRow(McpDocPatchTarget.ColumnDescription, Captured))
            .ThenRowsAffected(1)
            .ThenRowsAffected(1)
            .ThenRows(TableColumns)
            .ThenRowsAffected(0)
            .ThenThrow(new PostgresException("duplicate key", "ERROR", "ERROR", "23505"));
        var audit = new RecordingSecurityAudit();

        var act = () => Apply(capture, audit);

        (await act.Should().ThrowAsync<BeaconConflictException>()).Which.Message.Should().Be(ApplyDocumentationPatchHandler.TargetChangedMessage);
        audit.Events.Should().BeEmpty();
    }

    private static Task Apply(SqlCapture capture, RecordingSecurityAudit audit)
    {
        return new ApplyDocumentationPatchHandler(capture.Factory(), audit)
            .Handle(new ApplyDocumentationPatchCommand { PatchId = 31, AppliedByUserId = 42 }, CancellationToken.None);
    }

    // The patch row as EF reads it by id: id, then the other columns by name.
    private static object[] PatchRow(McpDocPatchTarget target, object currentContent)
    {
        var targetIdentifier = target == McpDocPatchTarget.ColumnDescription ? "public.orders.status" : "public.orders";

        return
        [
            31, DBNull.Value, DBNull.Value, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), currentContent, 10, DBNull.Value, 1, Proposed,
            "Asked about often.", DBNull.Value, (int)McpDocPatchStatus.Proposed, 3, targetIdentifier, (int)target
        ];
    }

    // The target row as the apply reads it: id and content, then (a column) its table's id, then the catalog's names.
    private static object[] TargetRow(
        McpDocPatchTarget target, object content, string schema = "public", string table = "orders", string column = "status")
    {
        return target == McpDocPatchTarget.ColumnDescription ? [8, content, 3, schema, table, column] : [3, content, schema, table];
    }

    // The value a command's WHERE compares `column` with: the parameter it names there.
    private static object? ComparedWith(SqlCapture capture, int command, string column)
    {
        var match = Regex.Match(WhereClauseOf(capture.Commands[command]), Regex.Escape(column) + @" = (@\w+)");
        match.Success.Should().BeTrue($"the write compares {column} with a parameter");
        var parameters = capture.CommandParameters[command];
        var name = match.Groups[1].Value;

        return parameters.TryGetValue(name, out var value) ? value : parameters[name.TrimStart('@')];
    }

    // The conditions of a write: what follows its WHERE.
    private static string WhereClauseOf(string command)
    {
        var where = command.LastIndexOf("WHERE", StringComparison.Ordinal);

        return where < 0 ? "" : command[where..];
    }

    private static SqlCapture Capture(bool sqlServer) => sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture();
}
