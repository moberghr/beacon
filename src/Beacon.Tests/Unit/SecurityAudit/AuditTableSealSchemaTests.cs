using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Interceptors;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services.Retention;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.SecurityAudit;

/// <summary>
/// Before the seal migration is applied: a sealed audit table found without its seal columns is written without the seal,
/// after the save and with no row lost, with one Warning; the finding is kept (found columns for good, missing ones read
/// again after a while); the content expiry clears the table as an unsealed store and goes on with the next one.
/// </summary>
[TestFixture]
public class AuditTableSealSchemaTests
{
    [Test]
    public async Task ARowOfATableWithoutItsSealColumns_IsWrittenWithoutTheSeal_AfterTheSave_WithOneWarning()
    {
        var logs = new LogRecorder();
        var schema = FakeSealProbe.Schema(true, loggerFactory: LoggerFactory.Create(x => x.AddProvider(logs)));
        var interceptor = new SecurityAuditInterceptor(SecurityAuditTestKit.Integrity, NullLoggerFactory.Instance, schema);
        var capture = new SqlCapture()
            .ThenRowsAffected(1)
            .ThenRowsAffected(1);
        await using var context = await capture.Factory(interceptor).CreateDbContextAsync();
        var first = History("Mcp:MaxRows");
        var second = History("Mcp:ForceReadOnly");
        context.AppSettingHistory.AddRange(first, second);

        await context.SaveChangesAsync();

        capture.Commands.Should().HaveCount(2, "each row is written on its own, once the save succeeded");
        capture.Commands.Should().OnlyContain(x => x.StartsWith("INSERT INTO beacon.app_setting_history"));
        capture.Commands[0].Should().Contain("setting_key").And.NotContain("hmac").And.NotContain("audit_guid").And.NotContain("seal_version");
        capture.CommandParameters[0].Values.Should().Contain("Mcp:MaxRows");
        first.Hmac.Should().BeNull("there is no place for the seal");
        logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning).Which.Message.Should().Contain("AddAuditTableSeal");
    }

    [Test]
    public void WithTheSealColumns_ARowIsSealed_AndStaysInEfsInsert()
    {
        var interceptor = new SecurityAuditInterceptor(SecurityAuditTestKit.Integrity, NullLoggerFactory.Instance, FakeSealProbe.Schema(false));
        using var context = NpgsqlTestContext.Create();
        var row = History("Mcp:MaxRows");
        context.AppSettingHistory.Add(row);

        interceptor.Seal(context);

        row.Hmac.Should().MatchRegex("^[0-9a-f]{64}$");
        context.Entry(row).State.Should().Be(Microsoft.EntityFrameworkCore.EntityState.Added);
    }

    [Test]
    public async Task TheFinding_IsKept_ColumnsFoundForGood_MissingOnesReadAgainAfterAWhile()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));
        var probe = new FakeSealProbe(true);
        var schema = new AuditTableSealSchema(probe, time, NullLoggerFactory.Instance);
        await using var context = NpgsqlTestContext.Create();

        schema.SealColumnsMissing(context, typeof(AppSettingHistory)).Should().BeTrue();
        schema.SealColumnsMissing(context, typeof(AppSettingHistory)).Should().BeTrue();
        probe.Reads.Should().Be(1, "a missing finding is kept for a while");

        probe.Missing = false;
        time.Advance(AuditTableSealSchema.RecheckMissingAfter);
        (await schema.SealColumnsMissingAsync(context, typeof(AppSettingHistory), CancellationToken.None)).Should().BeFalse("the migration was applied since");
        time.Advance(TimeSpan.FromDays(30));
        schema.SealColumnsMissing(context, typeof(AppSettingHistory)).Should().BeFalse();
        probe.Reads.Should().Be(2, "columns found are kept for good");

        probe.Missing = null;
        schema.SealColumnsMissing(context, typeof(ManualQueryExecutionLog)).Should().BeFalse("a table whose state cannot be read is taken as migrated");
    }

    [Test]
    public async Task TheContentExpiry_ClearsATableWithoutItsSealColumnsAsAnUnsealedStore_AndGoesOn()
    {
        var capture = new SqlCapture()
            .ThenNoRows()
            .ThenNoRows()
            .ThenNoRows()
            .ThenNoRows()
            .ThenNoRows()
            .ThenNoRows();
        await using var context = await capture.Factory().CreateDbContextAsync();

        var cleared = await ContentExpiry.ClearAsync(context, new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None, SecurityAuditTestKit.Integrity, FakeSealProbe.Schema(true));

        cleared.Should().HaveCount(6, "every store is read, the ones after the execution log too");
        var read = capture.Commands.Single(x => x.Contains("manual_query_execution_logs"));
        read.Should().NotContain("hmac", "the unsealed path reads ids only, never the missing seal columns");
    }

    private static AppSettingHistory History(string key)
    {
        return new AppSettingHistory
        {
            SettingKey = key,
            OldValue = "1",
            NewValue = "2",
            ChangedAt = new DateTime(2026, 10, 24, 9, 0, 0, DateTimeKind.Utc),
            ChangedByUserId = "ext-admin"
        };
    }
}
