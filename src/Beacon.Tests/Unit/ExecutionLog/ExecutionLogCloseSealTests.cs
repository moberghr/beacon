using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.ExecutionLog;

/// <summary>
/// Closing a sealed execution log row keeps its seal meaningful: the row is read and its seal verified, the close is
/// applied and the row sealed again over the closed row, and one update writes both only while the row is still Started
/// and carries the seal it was read with; a row whose seal does not verify is closed without a new seal; while the table
/// lacks its seal columns the row is closed as before, without reading it.
/// </summary>
[TestFixture]
public class ExecutionLogCloseSealTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task AnIntactRow_IsClosedAndSealedAgain_InOneConditionalUpdate()
    {
        var row = Sealed(Started());
        var readHmac = row.Hmac;
        var capture = new SqlCapture()
            .ThenRow(await ValuesAsync(row))
            .ThenRowsAffected(1);

        await Audit(capture.Factory(), sealColumnsMissing: false)
            .CompleteAsync(new SqlExecutionEntry(row.Id), new SqlExecutionEnd(QueryExecutionLogStatus.Succeeded, 12, 4.5));

        var expected = Started();
        expected.AuditGuid = row.AuditGuid;
        expected.Status = QueryExecutionLogStatus.Succeeded;
        expected.Success = true;
        expected.ResultCount = 12;
        expected.ExecutionTimeMs = 4.5;
        expected.CompletedTime = SecurityAuditIntegrity.StoredTime(Now.UtcDateTime);
        AuditTableSeal.Seal(expected, AuditTableStores.ExecutionLog, SecurityAuditTestKit.Integrity);
        capture.Commands.Should().HaveCount(2, "the row is read, then updated once");
        var update = capture.Commands[1];
        Set(update).Should().Contain("status").And.Contain("completed_time").And.Contain("hmac").And.Contain("seal_version");
        Where(update).Should().Contain("status = 1").And.Contain("hmac", "only a row still Started and carrying the seal that was verified is closed");
        capture.CommandParameters[1].Values.Should().Contain(expected.Hmac).And.Contain(readHmac);
        AuditTableSeal.Verify(expected, AuditTableStores.ExecutionLog, SecurityAuditTestKit.Integrity).Should().Be(SecurityAuditVerdicts.Intact);
    }

    [Test]
    public async Task ARowWhoseSealDoesNotVerify_IsClosedWithoutANewSeal()
    {
        var row = Sealed(Started());
        row.QueryText = "SELECT changed";
        var capture = new SqlCapture()
            .ThenRow(await ValuesAsync(row))
            .ThenRowsAffected(1);

        await Audit(capture.Factory(), sealColumnsMissing: false)
            .CompleteAsync(new SqlExecutionEntry(row.Id), new SqlExecutionEnd(QueryExecutionLogStatus.Failed, 0, 1, "failed"));

        Set(capture.Commands[1]).Should().Contain("status").And.NotContain("hmac", "a seal that fails is never replaced");
    }

    [Test]
    public async Task WithoutTheSealColumns_TheRowIsClosedAsBefore_WithoutReadingIt()
    {
        var capture = new SqlCapture().ThenRowsAffected(1);

        await Audit(capture.Factory(), sealColumnsMissing: true)
            .CompleteAsync(new SqlExecutionEntry(7), new SqlExecutionEnd(QueryExecutionLogStatus.Succeeded, 1, 1));

        var update = capture.Commands.Should().ContainSingle().Which;
        update.Should().StartWith("UPDATE beacon.manual_query_execution_logs").And.NotContain("hmac");
    }

    private static SqlExecutionAudit Audit(IDbContextFactory<BeaconContext> factory, bool sealColumnsMissing)
    {
        var actors = new Mock<IBeaconActorAccessor>();
        actors
            .Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(BeaconActor.Nobody);

        return new SqlExecutionAudit(
            factory,
            actors.Object,
            Options.Create(new SecurityAuditOptions()),
            NullLogger<SqlExecutionAudit>.Instance,
            new FakeTimeProvider(Now),
            SecurityAuditTestKit.Integrity,
            FakeSealProbe.Schema(sealColumnsMissing));
    }

    // The row's values in the order the read selects its columns.
    private static async Task<object[]> ValuesAsync(ManualQueryExecutionLog row)
    {
        var probe = new SqlCapture().ThenNoRows();
        await Audit(probe.Factory(), sealColumnsMissing: false)
            .CompleteAsync(new SqlExecutionEntry(row.Id), new SqlExecutionEnd(QueryExecutionLogStatus.Succeeded, 0, 0));
        var select = probe.Commands[0];
        var start = select.IndexOf("SELECT", StringComparison.Ordinal) + "SELECT".Length;
        var columns = select[start..select.IndexOf("FROM", start, StringComparison.Ordinal)]
            .Split(',')
            .Select(x => x.Trim().Split('.')[^1]);
        var values = new Dictionary<string, object?>
        {
            ["id"] = row.Id,
            ["audit_guid"] = row.AuditGuid,
            ["completed_time"] = row.CompletedTime,
            ["created_time"] = row.CreatedTime,
            ["data_source_id"] = row.DataSourceId,
            ["error_message"] = row.ErrorMessage,
            ["execution_context"] = row.ExecutionContext,
            ["execution_time_ms"] = row.ExecutionTimeMs,
            ["hmac"] = row.Hmac,
            ["hmac_key_id"] = row.HmacKeyId,
            ["query_text"] = row.QueryText,
            ["result_count"] = row.ResultCount,
            ["seal_version"] = row.SealVersion,
            ["status"] = (int?)row.Status,
            ["success"] = row.Success,
            ["triggered_by_user_id"] = row.TriggeredByUserId,
            ["user_id"] = row.UserId
        };

        return columns
            .Select(x => values[x] ?? DBNull.Value)
            .ToArray();
    }

    private static string Set(string update) => update[update.IndexOf("SET", StringComparison.Ordinal)..update.IndexOf("WHERE", StringComparison.Ordinal)];

    private static string Where(string update) => update[update.IndexOf("WHERE", StringComparison.Ordinal)..];

    private static ManualQueryExecutionLog Sealed(ManualQueryExecutionLog row)
    {
        AuditTableSeal.Seal(row, AuditTableStores.ExecutionLog, SecurityAuditTestKit.Integrity);

        return row;
    }

    private static ManualQueryExecutionLog Started()
    {
        return new ManualQueryExecutionLog
        {
            Id = 7,
            CreatedTime = Now.UtcDateTime.AddSeconds(-2),
            TriggeredByUserId = 3,
            QueryText = "SELECT 1",
            ResultCount = 0,
            ExecutionTimeMs = 0,
            DataSourceId = 4,
            ExecutionContext = "QueryStepPreview",
            Status = QueryExecutionLogStatus.Started,
            Success = false
        };
    }
}
