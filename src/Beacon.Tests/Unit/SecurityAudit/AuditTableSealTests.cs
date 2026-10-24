using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Interceptors;
using Beacon.Core.SecurityAudit;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.SecurityAudit;

/// <summary>
/// The seal of an execution log row and of a settings history row: an added row is sealed as it is saved, under the
/// security audit trail's current key; a change to any field, a seal only partly there and an unknown seal version fail; a
/// row with no seal at all is unsealed, never changed; a row sealed under an earlier key verifies once that key is a
/// previous one, and names a key Beacon does not hold once it is not; a row of one table never verifies as a row of the
/// other. The change tracker refuses changes to and deletes of stored rows.
/// </summary>
[TestFixture]
public class AuditTableSealTests
{
    private const string PreviousSecret = "the-previous-integrity-secret-of-this-test-0123";

    [Test]
    public void AnAddedExecutionLogRow_IsSealedOnSave_AndVerifiesIntact()
    {
        using var context = NpgsqlTestContext.Create();
        var row = ExecutionLogRow();
        context.ManualQueryExecutionLogs.Add(row);

        Interceptor().Seal(context);

        row.Should().BeEquivalentTo(new { SealVersion = AuditTableSeal.CurrentVersion, HmacKeyId = SecurityAuditTestKit.Integrity.CurrentKeyId }, x => x.ExcludingMissingMembers());
        row.AuditGuid.Should().NotBeNull();
        row.Hmac.Should().MatchRegex("^[0-9a-f]{64}$");
        (row.CreatedTime.Ticks % 10).Should().Be(0, "times are kept to the microsecond both providers store");
        AuditTableSeal.Verify(row, AuditTableStores.ExecutionLog, SecurityAuditTestKit.Integrity).Should().Be(SecurityAuditVerdicts.Intact);
    }

    [Test]
    public void AnAddedSettingHistoryRow_IsSealedOnSave_WithItsChangeTimeKeptToTheMicrosecond()
    {
        using var context = NpgsqlTestContext.Create();
        var row = SettingHistoryRow();
        context.AppSettingHistory.Add(row);

        Interceptor().Seal(context);

        row.Should().BeEquivalentTo(new { SealVersion = AuditTableSeal.CurrentVersion, HmacKeyId = SecurityAuditTestKit.Integrity.CurrentKeyId }, x => x.ExcludingMissingMembers());
        row.Hmac.Should().MatchRegex("^[0-9a-f]{64}$");
        (row.ChangedAt.Ticks % 10).Should().Be(0);
        AuditTableSeal.Verify(row, AuditTableStores.SettingHistory, SecurityAuditTestKit.Integrity).Should().Be(SecurityAuditVerdicts.Intact);
    }

    [TestCase("user")]
    [TestCase("text")]
    [TestCase("count")]
    [TestCase("duration")]
    [TestCase("dataSource")]
    [TestCase("context")]
    [TestCase("success")]
    [TestCase("error")]
    [TestCase("time")]
    [TestCase("guid")]
    [TestCase("keyId")]
    [TestCase("hmac")]
    public void AChangeToAnExecutionLogField_FailsVerification(string change)
    {
        var row = Sealed(ExecutionLogRow());

        switch (change)
        {
            case "user":
                row.UserId = "ext-other";
                break;
            case "text":
                row.QueryText = "SELECT 2";
                break;
            case "count":
                row.ResultCount = 0;
                break;
            case "duration":
                row.ExecutionTimeMs = 12.5000001;
                break;
            case "dataSource":
                row.DataSourceId = null;
                break;
            case "context":
                row.ExecutionContext = "FullQueryPreview";
                break;
            case "success":
                row.Success = false;
                break;
            case "error":
                row.ErrorMessage = "failed";
                break;
            case "time":
                row.CreatedTime = row.CreatedTime.AddSeconds(-1);
                break;
            case "guid":
                row.AuditGuid = Guid.NewGuid();
                break;
            case "keyId":
                row.HmacKeyId = SecurityAuditTestKit.Integrity.CurrentKeyId[..^1] + "0";
                break;
            case "hmac":
                row.Hmac = new string('a', 64);
                break;
        }

        AuditTableSeal.Verify(row, AuditTableStores.ExecutionLog, SecurityAuditTestKit.Integrity)
            .Should().BeOneOf([SecurityAuditVerdicts.Modified, SecurityAuditVerdicts.UnknownKey], "a {0} change is a changed row", change);
    }

    [TestCase("key")]
    [TestCase("old")]
    [TestCase("new")]
    [TestCase("changedAt")]
    [TestCase("changedBy")]
    public void AChangeToASettingHistoryField_FailsVerification(string change)
    {
        var row = Sealed(SettingHistoryRow());

        switch (change)
        {
            case "key":
                row.SettingKey = "Mcp:ForceReadOnly";
                break;
            case "old":
                row.OldValue = "false";
                break;
            case "new":
                row.NewValue = "true";
                break;
            case "changedAt":
                row.ChangedAt = row.ChangedAt.AddTicks(10);
                break;
            case "changedBy":
                row.ChangedByUserId = null;
                break;
        }

        AuditTableSeal.Verify(row, AuditTableStores.SettingHistory, SecurityAuditTestKit.Integrity).Should().Be(SecurityAuditVerdicts.Modified);
    }

    [Test]
    public void ARowWithNoSeal_IsUnsealed_AndASealOnlyPartlyThereOrOfAnotherVersion_IsChanged()
    {
        AuditTableSeal.Verify(ExecutionLogRow(), AuditTableStores.ExecutionLog, SecurityAuditTestKit.Integrity).Should().Be(McpAuditVerdicts.Unsealed);

        var partial = Sealed(ExecutionLogRow());
        partial.Hmac = null;
        AuditTableSeal.Verify(partial, AuditTableStores.ExecutionLog, SecurityAuditTestKit.Integrity).Should().Be(SecurityAuditVerdicts.Modified);

        var next = Sealed(SettingHistoryRow());
        next.SealVersion = AuditTableSeal.CurrentVersion + 1;
        AuditTableSeal.Verify(next, AuditTableStores.SettingHistory, SecurityAuditTestKit.Integrity).Should().Be(McpAuditVerdicts.UnknownVersion);

        var other = Sealed(SettingHistoryRow());
        other.SealVersion = AuditTableSeal.CurrentVersion + 2;
        AuditTableSeal.Verify(other, AuditTableStores.SettingHistory, SecurityAuditTestKit.Integrity).Should().Be(SecurityAuditVerdicts.Modified);
    }

    [Test]
    public void ARowSealedUnderAnEarlierKey_VerifiesOnceItIsAPreviousKey_AndNamesAnUnknownKeyOnceItIsNot()
    {
        var before = new SecurityAuditIntegrity(PreviousSecret, []);
        var row = Sealed(ExecutionLogRow(), before);
        var history = Sealed(SettingHistoryRow(), before);
        var rotated = new SecurityAuditIntegrity(SecurityAuditTestKit.TestSecret, [PreviousSecret]);
        var forgotten = new SecurityAuditIntegrity(SecurityAuditTestKit.TestSecret, []);

        row.HmacKeyId.Should().Be(before.CurrentKeyId).And.NotBe(rotated.CurrentKeyId);
        AuditTableSeal.Verify(row, AuditTableStores.ExecutionLog, rotated).Should().Be(SecurityAuditVerdicts.Intact);
        AuditTableSeal.Verify(history, AuditTableStores.SettingHistory, rotated).Should().Be(SecurityAuditVerdicts.Intact);
        AuditTableSeal.Verify(row, AuditTableStores.ExecutionLog, forgotten).Should().Be(SecurityAuditVerdicts.UnknownKey);
        Sealed(ExecutionLogRow(), rotated).HmacKeyId.Should().Be(rotated.CurrentKeyId, "new rows are sealed under the current key");
    }

    [Test]
    public void TheSealOfOneTable_IsNotTheSealOfAnother()
    {
        var history = Sealed(SettingHistoryRow());
        var log = ExecutionLogRow();
        log.CreatedTime = history.CreatedTime;
        log.AuditGuid = history.AuditGuid;
        log.SealVersion = history.SealVersion;
        log.HmacKeyId = history.HmacKeyId;
        log.Hmac = history.Hmac;

        AuditTableSeal.Verify(log, AuditTableStores.ExecutionLog, SecurityAuditTestKit.Integrity).Should().Be(SecurityAuditVerdicts.Modified);
    }

    [TestCase(EntityState.Modified)]
    [TestCase(EntityState.Deleted)]
    public void ASaveThatWouldChangeOrDeleteAStoredRow_IsRefused(EntityState state)
    {
        using var logContext = NpgsqlTestContext.Create();
        logContext.Entry(Sealed(ExecutionLogRow())).State = state;
        using var historyContext = NpgsqlTestContext.Create();
        historyContext.Entry(Sealed(SettingHistoryRow())).State = state;

        var changeLog = () => Interceptor().Seal(logContext);
        var changeHistory = () => Interceptor().Seal(historyContext);

        changeLog.Should().Throw<InvalidOperationException>().WithMessage("*append-only*");
        changeHistory.Should().Throw<InvalidOperationException>().WithMessage("*append-only*");
    }

    private static SecurityAuditInterceptor Interceptor()
    {
        return new SecurityAuditInterceptor(SecurityAuditTestKit.Integrity, NullLoggerFactory.Instance);
    }

    private static ManualQueryExecutionLog Sealed(ManualQueryExecutionLog row, SecurityAuditIntegrity? integrity = null)
    {
        AuditTableSeal.Seal(row, AuditTableStores.ExecutionLog, integrity ?? SecurityAuditTestKit.Integrity);

        return row;
    }

    private static AppSettingHistory Sealed(AppSettingHistory row, SecurityAuditIntegrity? integrity = null)
    {
        AuditTableSeal.Seal(row, AuditTableStores.SettingHistory, integrity ?? SecurityAuditTestKit.Integrity);

        return row;
    }

    private static ManualQueryExecutionLog ExecutionLogRow()
    {
        return new ManualQueryExecutionLog
        {
            Id = 7,
            CreatedTime = new DateTime(2026, 10, 24, 9, 0, 0, DateTimeKind.Utc).AddTicks(1234567),
            UserId = "ext-user",
            QueryText = "SELECT 1",
            ResultCount = 1,
            ExecutionTimeMs = 12.5,
            DataSourceId = 3,
            ExecutionContext = "QueryStepPreview",
            Success = true
        };
    }

    private static AppSettingHistory SettingHistoryRow()
    {
        return new AppSettingHistory
        {
            Id = 8,
            CreatedTime = new DateTime(2026, 10, 24, 9, 0, 0, DateTimeKind.Utc),
            SettingKey = "Mcp:MaxRows",
            OldValue = "100",
            NewValue = "200",
            ChangedAt = new DateTime(2026, 10, 24, 9, 0, 0, DateTimeKind.Utc).AddTicks(1234567),
            ChangedByUserId = "ext-admin"
        };
    }
}
