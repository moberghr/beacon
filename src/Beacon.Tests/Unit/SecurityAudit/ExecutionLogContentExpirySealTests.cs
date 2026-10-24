using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Entities;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services.Retention;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.SecurityAudit;

/// <summary>
/// The content expiry keeps the execution log's seals meaningful: a row whose seal verifies is cleared and sealed again over
/// what is left of it, by one update that applies only while the row still carries the seal it was read with; a row whose
/// seal does not verify is cleared without a new seal (it keeps failing verification); a row without a seal stays without
/// one. The verification's row count follows the data retention's execution-log window, and never the settings history.
/// </summary>
[TestFixture]
public class ExecutionLogContentExpirySealTests
{
    private static readonly DateTime Cutoff = new(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task AnIntactRow_IsClearedAndSealedAgain_AChangedRowIsClearedWithoutASeal_AndAnUnsealedOneStaysUnsealed()
    {
        var intact = Sealed(Row(1));
        var changed = Sealed(Row(2));
        changed.QueryText = "SELECT secret";
        var unsealed = Row(3);
        var columns = await SelectedColumnsAsync();
        var capture = new SqlCapture()
            .ThenRows(Values(columns, intact), Values(columns, changed), Values(columns, unsealed))
            .ThenRowsAffected(1)
            .ThenRowsAffected(1)
            .ThenRowsAffected(1);
        await using var context = await capture.Factory().CreateDbContextAsync();
        var readHmac = intact.Hmac;

        var cleared = await ContentExpiry.ClearSealedAsync(context.ManualQueryExecutionLogs, AuditTableStores.ExecutionLog, Cutoff, SecurityAuditTestKit.Integrity, CancellationToken.None);

        cleared.Should().Be(3);
        var expected = Row(1);
        expected.AuditGuid = intact.AuditGuid;
        expected.QueryText = string.Empty;
        expected.ErrorMessage = McpContentRedactor.ClassSchema;
        AuditTableSeal.Seal(expected, AuditTableStores.ExecutionLog, SecurityAuditTestKit.Integrity);
        var update = capture.Commands[1];
        Set(update).Should().Contain("query_text").And.Contain("error_message").And.Contain("hmac").And.Contain("seal_version");
        Where(update).Should().Contain("hmac", "the update applies only while the row carries the seal that was verified");
        capture.CommandParameters[1].Values.Should().Contain(expected.Hmac).And.Contain(readHmac).And.Contain(McpContentRedactor.ClassSchema);
        AuditTableSeal.Verify(expected, AuditTableStores.ExecutionLog, SecurityAuditTestKit.Integrity).Should().Be(SecurityAuditVerdicts.Intact, "the cleared row verifies under its new seal");
        Set(capture.Commands[2]).Should().Contain("query_text").And.NotContain("hmac", "a row whose seal fails is not sealed again");
        Set(capture.Commands[3]).Should().Contain("query_text").And.NotContain("hmac", "a row without a seal stays without one");
    }

    // A seal this node cannot read (the next seal version during an upgrade, or a key it no longer holds) is never
    // destroyed: the row is left whole for a node that can verify it.
    [TestCase("version")]
    [TestCase("key")]
    public async Task ARowWhoseSealThisNodeCannotRead_IsLeftWhole(string unreadable)
    {
        var row = unreadable == "key"
            ? SealedUnder(Row(1), new SecurityAuditIntegrity("a-key-this-node-does-not-hold-0123456789abcdef", []))
            : Sealed(Row(1));
        if (unreadable == "version")
        {
            row.SealVersion = AuditTableSeal.CurrentVersion + 1;
        }

        var columns = await SelectedColumnsAsync();
        var capture = new SqlCapture().ThenRows(Values(columns, row));
        await using var context = await capture.Factory().CreateDbContextAsync();
        var logs = new LogRecorder();

        var cleared = await ContentExpiry.ClearSealedAsync(
            context.ManualQueryExecutionLogs,
            AuditTableStores.ExecutionLog,
            Cutoff,
            SecurityAuditTestKit.Integrity,
            CancellationToken.None,
            LoggerFactory.Create(x => x.AddProvider(logs)).CreateLogger("test"));

        cleared.Should().Be(0);
        capture.Commands.Should().ContainSingle("the row is read, never updated");
        logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Information)
            .Which.Message.Should().Contain("cannot verify");
    }

    [Test]
    public void TheVerificationsRowCount_FollowsTheExecutionLogRetentionWindow_AndNeverTheSettingsHistory()
    {
        var retention = new DataRetentionAuditTableRetention(Options.Create(new RetentionOptions
        {
            Days = 90,
            Targets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [RetentionTargetNames.ExecutionLog] = 30 }
        }));
        var forever = new DataRetentionAuditTableRetention(Options.Create(new RetentionOptions()));

        retention.RetentionDaysOf(AuditTableStores.ExecutionLog.Name).Should().Be(30);
        retention.RetentionDaysOf(AuditTableStores.SettingHistory.Name).Should().BeNull("nothing purges the settings history");
        forever.RetentionDaysOf(AuditTableStores.ExecutionLog.Name).Should().BeNull("0 keeps the rows forever");
    }

    // The columns the read selects, in order: the rows are answered in that order.
    private static async Task<List<string>> SelectedColumnsAsync()
    {
        var capture = new SqlCapture().ThenNoRows();
        await using var context = await capture.Factory().CreateDbContextAsync();
        await ContentExpiry.ClearSealedAsync(context.ManualQueryExecutionLogs, AuditTableStores.ExecutionLog, Cutoff, SecurityAuditTestKit.Integrity, CancellationToken.None);
        var select = capture.Commands[0];
        var start = select.IndexOf("SELECT", StringComparison.Ordinal) + "SELECT".Length;
        var list = select[start..select.IndexOf("FROM", start, StringComparison.Ordinal)];

        return list
            .Split(',')
            .Select(x => x.Trim().Split('.')[^1])
            .ToList();
    }

    private static object[] Values(List<string> columns, ManualQueryExecutionLog row)
    {
        var values = new Dictionary<string, object?>
        {
            ["id"] = row.Id,
            ["audit_guid"] = row.AuditGuid,
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
            ["success"] = row.Success,
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
        return SealedUnder(row, SecurityAuditTestKit.Integrity);
    }

    private static ManualQueryExecutionLog SealedUnder(ManualQueryExecutionLog row, SecurityAuditIntegrity integrity)
    {
        AuditTableSeal.Seal(row, AuditTableStores.ExecutionLog, integrity);

        return row;
    }

    private static ManualQueryExecutionLog Row(int id)
    {
        return new ManualQueryExecutionLog
        {
            Id = id,
            CreatedTime = Cutoff.AddDays(-5),
            UserId = "ext-user",
            QueryText = "SELECT 1",
            ResultCount = 1,
            ExecutionTimeMs = 2.5,
            DataSourceId = 4,
            ExecutionContext = "QueryStepPreview",
            Success = false,
            ErrorMessage = "relation \"orders\" does not exist"
        };
    }
}
