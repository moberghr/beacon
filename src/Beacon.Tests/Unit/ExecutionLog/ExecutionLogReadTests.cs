using System.Text;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.ExecutionLog;
using Beacon.Core.SecurityAudit;
using Beacon.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ExecutionLog;

/// <summary>
/// The execution log's paged read and CSV export: a bounded window, the rows of the data sources the caller may read, and
/// a security audit event with the filters' shape before anything is handed out. Captured SQL; no database (§4.7).
/// </summary>
[TestFixture]
public class ExecutionLogReadTests
{
    private static readonly DateTime From = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task ReadingTheLog_IsRecorded_WithTheFiltersShape()
    {
        var capture = new SqlCapture().AnsweringCountsWithOne();
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(9, "ext-auditor"));
        var handler = new GetExecutionLogHandler(capture.Factory(), TestProjectAccess.Evaluator(TestProjectAccess.FastPath), SecurityAuditTestKit.Reads(capture.Factory(), audit));

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
        var handler = new GetExecutionLogHandler(capture.Factory(), TestProjectAccess.Evaluator(TestProjectAccess.FastPath), SecurityAuditTestKit.Reads(capture.Factory(), audit));

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
        var handler = new ExportExecutionLogHandler(capture.Factory(), TestProjectAccess.Evaluator(TestProjectAccess.FastPath), SecurityAuditTestKit.Reads(capture.Factory(), audit));

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
}
