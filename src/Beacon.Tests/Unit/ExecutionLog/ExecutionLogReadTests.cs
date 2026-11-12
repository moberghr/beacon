using System.Text;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.ExecutionLog;
using Beacon.Core.SecurityAudit;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ExecutionLog;

/// <summary>
/// The execution log's paged read and CSV export: a bounded window, every row for an Auditor and the rows of the data
/// sources the caller may read for anyone else, and a security audit event with the filters' shape before anything is
/// handed out. Captured SQL; no database (§4.7).
/// </summary>
[TestFixture]
public class ExecutionLogReadTests
{
    private static readonly DateTime From = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));

    [Test]
    public async Task ReadingTheLog_IsRecorded_WithTheFiltersShape()
    {
        var capture = new SqlCapture().AnsweringCountsWithOne();
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(9, "ext-auditor"));
        var handler = ReadHandler(capture, audit);

        await handler.Handle(
            new GetExecutionLogQuery { From = From, To = From.AddDays(7), DataSourceId = 3, Status = QueryExecutionLogStatus.Started },
            CancellationToken.None);

        capture.Commands.Should().OnlyContain(x => x.Contains("manual_query_execution_logs"));
        var read = audit.Events.Should().ContainSingle().Subject;
        read.Action.Should().Be(SecurityAuditActions.ExecutionLogRead);
        read.ResourceType.Should().Be(SecurityAuditResources.ExecutionLog);
        read.ActorUserId.Should().Be(9);
        read.DetailsJson.Should().Contain("\"dataSourceId\":3").And.Contain("\"status\":\"Started\"");
    }

    [Test]
    public async Task AWindowLongerThanAllowed_IsRefused_BeforeAnythingIsRead()
    {
        var capture = new SqlCapture();
        var audit = new RecordingSecurityAudit();
        var handler = ReadHandler(capture, audit);

        var act = () => handler.Handle(new GetExecutionLogQuery { From = From, To = From.AddDays(GetExecutionLogHandler.MaxRangeDays + 1) }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        capture.Commands.Should().BeEmpty();
        audit.Events.Should().BeEmpty();
    }

    [Test]
    public async Task TheExport_IsACsvOfTheWindow_NewestFirst_RecordedWithItsRowCount()
    {
        var capture = new SqlCapture().ThenNoRows();
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(9, "ext-auditor"));
        var handler = ExportHandler(capture, audit);

        var export = await handler.Handle(new ExportExecutionLogQuery { From = From, To = From.AddDays(7) }, CancellationToken.None);

        var select = capture.Commands.Should().ContainSingle().Subject;
        select.Should().Contain("ORDER BY m.created_time DESC, m.id DESC").And.Contain("LIMIT");
        capture.CommandParameters.Single().Values.Should().Contain(ExportExecutionLogHandler.MaxRows + 1, "one row past the cap tells whether the file holds every match");
        Encoding.UTF8.GetString(export.Content).TrimStart('﻿').Should().StartWith("id,createdTime,completedTime,status,success,executionContext,dataSourceId,triggeredByUserId,");
        export.Rows.Should().Be(0);
        export.Truncated.Should().BeFalse();
        export.FileName.Should().Be("execution-log-20261001-20261008.csv");
        var exported = audit.Events.Should().ContainSingle().Subject;
        exported.Action.Should().Be(SecurityAuditActions.ExecutionLogExported);
        exported.DetailsJson.Should().Contain("\"rows\":0").And.Contain("\"truncated\":false");
    }

    [Test]
    public async Task TheReadShowsTheStatement_WithItsLiteralsMasked()
    {
        var capture = new SqlCapture()
            .ThenScalar(1)
            .ThenRows([7, From, DBNull.Value, 2, true, "QueryStep", 3, "core", 5, 1, 4.0, "SELECT id FROM accounts WHERE email = 'ana@example.com' AND age > 40", DBNull.Value, 1, 1]);
        var handler = ReadHandler(capture, new RecordingSecurityAudit());

        var page = await handler.Handle(new GetExecutionLogQuery { From = From, To = From.AddDays(7) }, CancellationToken.None);

        page.Items.Should().ContainSingle()
            .Which.QueryText.Should().Be("SELECT id FROM accounts WHERE email = ? AND age > ?");
    }

    [Test]
    public async Task AStartedRowPastTheStatementTimeout_IsShownAbandoned_AndFilteredSo()
    {
        var capture = new SqlCapture().AnsweringCountsWithOne();
        var handler = ReadHandler(capture, new RecordingSecurityAudit());

        await handler.Handle(new GetExecutionLogQuery { From = From, To = From.AddDays(7), Status = QueryExecutionLogStatus.Abandoned }, CancellationToken.None);

        var abandonedBefore = Clock.GetUtcNow().UtcDateTime - TimeSpan.FromSeconds(new QueryExecutionOptions().StatementTimeoutSeconds) - ExecutionLogView.AbandonedMargin;
        capture.Commands.Should().AllSatisfy(x => x.Should().Contain("m.status = 1").And.Contain("m.created_time < @abandonedBefore"));
        capture.Commands.Last().Should().Contain("THEN 6", "a Started row past the timeout is shown as abandoned");
        capture.CommandParameters.Should().AllSatisfy(x => x.Values.Should().Contain(abandonedBefore));
    }

    [Test]
    public async Task TheExport_MasksLiterals_WritesFormulaLikeCellsAsText_AndCutsLongStatements()
    {
        var longSql = "SELECT " + new string('a', ExportExecutionLogHandler.MaxQueryTextChars) + " FROM t";
        var capture = new SqlCapture().ThenRows(
            [7, From, DBNull.Value, 3, false, "@QueryStep", 3, 5, 0, 4.0, "+cmd|' /C calc'!A0", "SELECT id FROM accounts WHERE email = 'ana@example.com'", 1, 1],
            [8, From, DBNull.Value, 2, true, "QueryStep", 3, 5, 1, 4.0, DBNull.Value, "-1 + id", 1, 1],
            [9, From, DBNull.Value, 2, true, "QueryStep", 3, 5, 1, 4.0, DBNull.Value, longSql[..(ExportExecutionLogHandler.MaxQueryTextChars + 1)], 1, 1]);
        var handler = ExportHandler(capture, new RecordingSecurityAudit());

        var export = await handler.Handle(new ExportExecutionLogQuery { From = From, To = From.AddDays(7) }, CancellationToken.None);

        var csv = Encoding.UTF8.GetString(export.Content);
        csv.Should().NotContain("ana@example.com")
            .And.Contain("SELECT id FROM accounts WHERE email = ?")
            .And.Contain(",'@QueryStep,")
            .And.Contain(",'+cmd|' /C calc'!A0,", "a cell starting with + is written as text")
            .And.Contain(",'-? + id");
        var lines = csv.Split("\r\n");
        lines[3].Should().EndWith(ExportExecutionLogHandler.CutMarker);
        capture.Commands.Single().Should().Contain("substring(m0.query_text, 1, 4001)");
    }

    // The Auditor reads the execution log as it reads the security audit trail: every row, those whose data source is gone
    // included, without a project membership (a functional role grants no project data, so its project scope is empty).
    [Test]
    public async Task AnAuditorWithoutAProject_ReadsAndExportsEveryRow()
    {
        var auditor = TestPermissions.Holding(BeaconPermissions.Auditor);
        var read = new SqlCapture().AnsweringCountsWithOne();
        var export = new SqlCapture().ThenNoRows();

        await ReadHandler(read, new RecordingSecurityAudit(), ProjectAccessScope.None, auditor)
            .Handle(new GetExecutionLogQuery { From = From, To = From.AddDays(7) }, CancellationToken.None);
        await ExportHandler(export, new RecordingSecurityAudit(), ProjectAccessScope.None, auditor)
            .Handle(new ExportExecutionLogQuery { From = From, To = From.AddDays(7) }, CancellationToken.None);

        var statements = read.Commands.Concat(export.Commands).ToList();
        statements.Should().HaveCount(3);
        statements.Should().OnlyContain(x => WindowOnly(x), "nothing but the window narrows an Auditor's read");
        statements.Should().NotContain(x => x.Contains("project_data_sources") || x.Contains("data_source_id IS NOT NULL"));
    }

    // A SecurityAdmin who is no Auditor keeps the read of the data sources it may read: without a project, no row.
    [Test]
    public async Task ASecurityAdminWhoIsNoAuditor_ReadsOnlyTheDataSourcesItMayRead()
    {
        var securityAdmin = TestPermissions.Holding(BeaconPermissions.SecurityAdmin);
        var noProject = new SqlCapture().AnsweringCountsWithOne();
        var restricted = new SqlCapture().AnsweringCountsWithOne();
        var query = new GetExecutionLogQuery { From = From, To = From.AddDays(7) };

        await ReadHandler(noProject, new RecordingSecurityAudit(), ProjectAccessScope.None, securityAdmin).Handle(query, CancellationToken.None);
        await ReadHandler(restricted, new RecordingSecurityAudit(), TestProjectAccess.ReadableIn(1), securityAdmin).Handle(query, CancellationToken.None);

        noProject.Commands.Should().NotBeEmpty().And.OnlyContain(x => !WindowOnly(x), "the read is narrowed to the readable sources");
        restricted.Commands.Should().NotBeEmpty().And.OnlyContain(x => x.Contains("project_data_sources"));
    }

    // A statement whose only condition is the window.
    private static bool WindowOnly(string sql)
    {
        const string window = "WHERE m.created_time >= @from AND m.created_time <= @to";

        return sql.Contains(window + "\n") || sql.EndsWith(window, StringComparison.Ordinal);
    }

    private static GetExecutionLogHandler ReadHandler(
        SqlCapture capture,
        RecordingSecurityAudit audit,
        ProjectAccessScope? scope = null,
        IBeaconPermissionAccessor? permissions = null) =>
        new(
            capture.Factory(),
            TestProjectAccess.Evaluator(scope ?? TestProjectAccess.FastPath),
            permissions ?? TestPermissions.None,
            SecurityAuditTestKit.Reads(capture.Factory(), audit),
            Options.Create(new QueryExecutionOptions()),
            Clock);

    private static ExportExecutionLogHandler ExportHandler(
        SqlCapture capture,
        RecordingSecurityAudit audit,
        ProjectAccessScope? scope = null,
        IBeaconPermissionAccessor? permissions = null) =>
        new(
            capture.Factory(),
            TestProjectAccess.Evaluator(scope ?? TestProjectAccess.FastPath),
            permissions ?? TestPermissions.None,
            SecurityAuditTestKit.Reads(capture.Factory(), audit),
            Options.Create(new QueryExecutionOptions()),
            Clock);
}
