using Beacon.AI.Services.Learning;
using Beacon.Core.Models;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace Beacon.Tests.Integration;

/// <summary>
/// Translation coverage (§4.3) for the reads of <see cref="KnowledgeItemStatsService"/>, run as the service sends them on
/// both providers (no database: <see cref="SqlCapture"/>). The signal read carries the project predicate on the signals and
/// on the follow-up re-runs (§1.12) and selects no text column but the caller hash (§1.11); the stats read carries the
/// project predicate.
/// </summary>
[TestFixture]
public class KnowledgeItemStatsTranslationTests
{
    private const int ProjectId = 42;

    [Test]
    public async Task KnowledgeItemSignalRead_Translates()
    {
        var capture = await RecomputeCapturingAsync(new SqlCapture());
        var project = ProjectParameter(capture, 0);

        var signalRead = capture.Commands[0];
        signalRead.Should().Contain($"m.project_id = @{project}");
        signalRead.Should().Contain("m.tool = 'ask'");
        signalRead.Should().Contain("m.created_time >= @cutoff");
        signalRead.Should().Contain("m1.parent_signal_id = m.id").And.Contain($"m1.project_id = @{project}");
        signalRead.Should().Contain("(m1.tool = 'query' OR m1.tool = 'dry_run')").And.Contain("m1.is_edited_rerun");
        signalRead.Should().Contain("LEFT JOIN beacon.mcp_signal_context_items");
        AssertSelectsNoTextButTheCallerHash(signalRead, "m.caller_hash");
    }

    [Test]
    public async Task KnowledgeItemSignalRead_SqlServer_Translates()
    {
        var capture = await RecomputeCapturingAsync(SqlCapture.OnSqlServer());
        var project = ProjectParameter(capture, 0);

        var signalRead = capture.Commands[0];
        signalRead.Should().Contain($"[m].[ProjectId] = @{project}");
        signalRead.Should().Contain("[m].[Tool] = N'ask'");
        signalRead.Should().Contain("[m].[CreatedTime] >= @cutoff");
        signalRead.Should().Contain("[m1].[ParentSignalId] = [m].[Id]").And.Contain($"[m1].[ProjectId] = @{project}");
        signalRead.Should().Contain("([m1].[Tool] = N'query' OR [m1].[Tool] = N'dry_run')").And.Contain("[m1].[IsEditedRerun]");
        signalRead.Should().Contain("LEFT JOIN [beacon].[McpSignalContextItems]");
        AssertSelectsNoTextButTheCallerHash(signalRead, "[m].[CallerHash]");
    }

    [Test]
    public async Task KnowledgeItemStatRead_Translates()
    {
        var capture = await RecomputeCapturingAsync(new SqlCapture());

        capture.Commands[1].Should().Contain("FROM beacon.mcp_knowledge_item_stats AS m")
            .And.Contain($"WHERE m.project_id = @{ProjectParameter(capture, 1)}");
    }

    [Test]
    public async Task KnowledgeItemStatRead_SqlServer_Translates()
    {
        var capture = await RecomputeCapturingAsync(SqlCapture.OnSqlServer());

        capture.Commands[1].Should().Contain("FROM [beacon].[McpKnowledgeItemStats] AS [m]")
            .And.Contain($"WHERE [m].[ProjectId] = @{ProjectParameter(capture, 1)}");
    }

    // Runs the recompute with no signals and no stored rows: the two reads are sent, and with nothing to write the save sends
    // nothing.
    private static async Task<SqlCapture> RecomputeCapturingAsync(SqlCapture capture)
    {
        capture
            .ThenNoRows()
            .ThenNoRows();
        var service = new KnowledgeItemStatsService(
            capture.Factory(),
            SettingsProviderMock.Create(new McpSettingsData()).Object,
            new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero)));

        await service.RecomputeAsync(ProjectId, CancellationToken.None);

        capture.Commands.Should().HaveCount(2);

        return capture;
    }

    // The name of the parameter the command was sent the project's id in (the compiler names the captured variable).
    private static string ProjectParameter(SqlCapture capture, int command)
    {
        return capture.CommandParameters[command]
            .Where(x => Equals(x.Value, ProjectId))
            .Select(x => x.Key.TrimStart('@'))
            .Should().ContainSingle().Subject;
    }

    // The select list (up to the outer FROM, which starts a line) holds ids, enums, flags and the caller hash: no question,
    // SQL, note, error or other text column.
    private static void AssertSelectsNoTextButTheCallerHash(string sql, string callerHash)
    {
        var selected = sql[..sql.IndexOf("\nFROM ", StringComparison.Ordinal)];
        selected.Should().Contain(callerHash);
        foreach (var text in new[] { "question", "sql", "note", "error", "tables", "columns", "intent", "routing" })
        {
            selected.Should().NotContainEquivalentOf(text);
        }
    }
}
