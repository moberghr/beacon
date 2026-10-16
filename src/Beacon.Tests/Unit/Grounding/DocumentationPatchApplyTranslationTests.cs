using System.Text.RegularExpressions;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.McpLearning;
using Beacon.Core.Models;
using Beacon.Core.SecurityAudit;
using Beacon.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Grounding;

/// <summary>
/// Applying a column or table description patch, as PostgreSQL and SQL Server receive it, captured without a database
/// (§4.7): the patch and the target are read, then the description is written in one statement that also compares it with
/// the content captured when the patch was proposed (<c>IS NULL</c> when none was), then the patch is marked applied in one
/// statement conditional on it still being proposed. A description write that changes nothing (another write landed
/// between the read and the write) refuses the patch: the patch is not marked applied and no event is recorded.
/// </summary>
[TestFixture]
public class DocumentationPatchApplyTranslationTests
{
    private const string Captured = "The order's state.";
    private const string Proposed = "Where the order is in fulfilment.";

    [TestCase(false, McpDocPatchTarget.ColumnDescription, "UPDATE beacon.column_metadata AS c", "c.id = @", "c.description = @", "SET description = @")]
    [TestCase(true, McpDocPatchTarget.ColumnDescription, "UPDATE [c]", "[c].[Id] = @", "[c].[Description] = @", "SET [c].[Description] = @")]
    [TestCase(false, McpDocPatchTarget.TableDescription, "UPDATE beacon.database_metadata AS d", "d.id = @", "d.table_description = @", "SET table_description = @")]
    [TestCase(true, McpDocPatchTarget.TableDescription, "UPDATE [d]", "[d].[Id] = @", "[d].[TableDescription] = @", "SET [d].[TableDescription] = @")]
    public async Task ADescriptionPatch_IsWrittenOnlyWhileTheTargetHoldsTheCapturedContent_Translates(
        bool sqlServer, McpDocPatchTarget target, string update, string byId, string compared, string written)
    {
        var capture = Capture(sqlServer)
            .ThenRow(PatchRow(target, Captured))
            .ThenRow(8, Captured)
            .ThenRowsAffected(1)
            .ThenRowsAffected(1);
        var audit = new RecordingSecurityAudit();

        await Apply(capture, audit);

        capture.Commands.Should().HaveCount(4, "the patch, the target, the conditional description write, then the patch's status");
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
            .ThenRow(8, DBNull.Value)
            .ThenRowsAffected(1)
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
            .ThenRow(8, Captured)
            .ThenRowsAffected(0);
        var audit = new RecordingSecurityAudit();

        var act = () => Apply(capture, audit);

        (await act.Should().ThrowAsync<BeaconConflictException>()).Which.Message.Should().Be(ApplyDocumentationPatchHandler.TargetChangedMessage);
        capture.Commands.Should().HaveCount(3, "the patch's status is not written");
        audit.Events.Should().BeEmpty();
    }

    // The target read holds other content than the patch was proposed on: refused before any write.
    [TestCase(false)]
    [TestCase(true)]
    public async Task ATargetHoldingOtherContent_IsRefusedWithoutAWrite_Translates(bool sqlServer)
    {
        var capture = Capture(sqlServer)
            .ThenRow(PatchRow(McpDocPatchTarget.ColumnDescription, Captured))
            .ThenRow(8, "the order's state.");

        var act = () => Apply(capture, new RecordingSecurityAudit());

        await act.Should().ThrowAsync<BeaconConflictException>();
        capture.Commands.Should().HaveCount(2, "the patch and the target are read; nothing is written");
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
            31, DBNull.Value, DBNull.Value, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), currentContent, 10, 1, Proposed,
            "Asked about often.", (int)McpDocPatchStatus.Proposed, 3, targetIdentifier, (int)target
        ];
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
