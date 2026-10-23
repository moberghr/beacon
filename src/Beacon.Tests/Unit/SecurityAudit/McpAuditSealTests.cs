using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Core;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Interceptors;
using Beacon.Core.Mcp;
using Beacon.Core.PostgreSql;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services.Retention;
using Beacon.Core.SqlServer;
using Beacon.Core.Worker;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.SecurityAudit;

/// <summary>
/// The seal of an MCP audit row: an added row is sealed with its statements, after the content retention lock has stripped
/// what it strips, under the security audit trail's current key; a change to any field of the row or of a statement, a
/// statement removed, added or moved to another row, a seal only partly there and an unknown seal version fail; a row with
/// no seal at all is unsealed, never changed; a row sealed under an earlier key verifies once that key is a previous one;
/// text no encoder accepts seals. The change tracker refuses changes to stored rows and statements, and statements saved
/// without their row. Both providers run the content retention lock before the seal.
/// </summary>
[TestFixture]
public class McpAuditSealTests
{
    private const string PreviousSecret = "the-previous-integrity-secret-of-this-test-0123";

    [Test]
    public void AnAddedRow_IsSealedWithItsStatements_AndVerifiesIntact()
    {
        using var context = NpgsqlTestContext.Create();
        var row = Row();
        context.McpAuditLogs.Add(row);
        row.ResultRowCount = 12;

        Interceptor().Seal(context);

        row.Should().BeEquivalentTo(new { SealVersion = McpAuditSeal.CurrentVersion, HmacKeyId = SecurityAuditTestKit.Integrity.CurrentKeyId }, x => x.ExcludingMissingMembers());
        row.AuditGuid.Should().NotBeNull();
        row.Hmac.Should().MatchRegex("^[0-9a-f]{64}$");
        (row.CreatedTime.Ticks % 10).Should().Be(0, "times are kept to the microsecond both providers store");
        McpAuditSeal.Verify(row, Read(row), SecurityAuditTestKit.Integrity).Should().Be(SecurityAuditVerdicts.Intact, "the seal covers the value saved");
    }

    [TestCase("parameters")]
    [TestCase("error")]
    [TestCase("user")]
    [TestCase("time")]
    [TestCase("guid")]
    [TestCase("statementSql")]
    [TestCase("statementRows")]
    [TestCase("statementRemoved")]
    [TestCase("statementAdded")]
    [TestCase("keyId")]
    [TestCase("hmac")]
    public void AChangeToTheRowOrAStatement_FailsVerification(string change)
    {
        var row = SealedRow();
        var statements = Read(row);

        switch (change)
        {
            case "parameters":
                row.Parameters = "{\"question\":\"other\"}";
                break;
            case "error":
                row.ErrorMessage = null;
                break;
            case "user":
                row.UserId = 4;
                break;
            case "time":
                row.CreatedTime = row.CreatedTime.AddSeconds(-1);
                break;
            case "guid":
                row.AuditGuid = Guid.NewGuid();
                break;
            case "statementSql":
                statements[0].Sql = "SELECT 2";
                break;
            case "statementRows":
                statements[1].RowCount = 0;
                break;
            case "statementRemoved":
                statements.RemoveAt(0);
                break;
            case "statementAdded":
                statements.Add(new McpAuditStatement { Purpose = McpStatementPurposes.Probe, Sql = "SELECT 3", CreatedTime = statements[0].CreatedTime });
                break;
            case "keyId":
                row.HmacKeyId = SecurityAuditTestKit.Integrity.CurrentKeyId[..^1] + "0";
                break;
            case "hmac":
                row.Hmac = new string('a', 64);
                break;
        }

        McpAuditSeal.Verify(row, statements, SecurityAuditTestKit.Integrity)
            .Should().BeOneOf([SecurityAuditVerdicts.Modified, SecurityAuditVerdicts.UnknownKey], "a {0} change is a changed row", change);
    }

    [Test]
    public void AStatementMovedToAnotherRow_FailsBothRows()
    {
        var first = SealedRow();
        var second = SealedRow();
        var firstStatements = Read(first);
        var secondStatements = Read(second);

        secondStatements.Add(firstStatements[0]);
        firstStatements.RemoveAt(0);

        McpAuditSeal.Verify(first, firstStatements, SecurityAuditTestKit.Integrity).Should().Be(SecurityAuditVerdicts.Modified);
        McpAuditSeal.Verify(second, secondStatements, SecurityAuditTestKit.Integrity).Should().Be(SecurityAuditVerdicts.Modified);
    }

    [Test]
    public void TheStatements_VerifyInWhateverOrderTheyAreRead()
    {
        var row = SealedRow();
        var statements = Read(row);

        statements.Reverse();

        McpAuditSeal.Verify(row, statements, SecurityAuditTestKit.Integrity).Should().Be(SecurityAuditVerdicts.Intact);
    }

    [Test]
    public void ARowWithNoSeal_IsUnsealed_AndOneWithOnlyPartOfASealOrAnUnknownVersion_IsChanged()
    {
        var unsealed = Row();
        var partial = SealedRow();
        partial.Hmac = null;
        var unknownVersion = SealedRow();
        unknownVersion.SealVersion = McpAuditSeal.CurrentVersion + 1;

        McpAuditSeal.Verify(unsealed, Read(unsealed), SecurityAuditTestKit.Integrity).Should().Be(McpAuditVerdicts.Unsealed);
        McpAuditSeal.Verify(partial, Read(partial), SecurityAuditTestKit.Integrity).Should().Be(SecurityAuditVerdicts.Modified);
        McpAuditSeal.Verify(unknownVersion, Read(unknownVersion), SecurityAuditTestKit.Integrity).Should().Be(SecurityAuditVerdicts.Modified);
    }

    [Test]
    public void ARowSealedUnderAnEarlierKey_VerifiesOnceThatKeyIsAPreviousOne_AndIsUnknownWithoutIt()
    {
        var earlier = new SecurityAuditIntegrity(PreviousSecret, []);
        var row = SealedRow(earlier);
        var rotated = new SecurityAuditIntegrity("the-current-integrity-secret-of-this-test-0123", [PreviousSecret]);
        var forgotten = new SecurityAuditIntegrity("the-current-integrity-secret-of-this-test-0123", []);

        McpAuditSeal.Verify(row, Read(row), rotated).Should().Be(SecurityAuditVerdicts.Intact);
        McpAuditSeal.Verify(row, Read(row), forgotten).Should().Be(SecurityAuditVerdicts.UnknownKey);
    }

    // The store has a key of its own: the same fields under the same root key never verify as another store's row.
    [Test]
    public void TheSeal_IsKeyedForTheMcpAuditOnly()
    {
        var canonical = "the same bytes"u8.ToArray();

        var sealedForMcp = SecurityAuditTestKit.Integrity.SealRecord(McpAuditSeal.Store, canonical);

        SecurityAuditTestKit.Integrity.VerifyRecord("another_store", sealedForMcp.KeyId, sealedForMcp.Hmac, canonical).Should().Be(SecurityAuditVerdict.Modified);
        SecurityAuditTestKit.Integrity.VerifyRecord(McpAuditSeal.Store, sealedForMcp.KeyId, sealedForMcp.Hmac, canonical).Should().Be(SecurityAuditVerdict.Intact);
    }

    [Test]
    public void TextNoEncoderAccepts_IsSealed_AndAChangeToItStillFails()
    {
        var row = Row();
        row.Parameters = "{\"question\":\"\ud800\"}";

        McpAuditSeal.Seal(row, SecurityAuditTestKit.Integrity);
        var intact = McpAuditSeal.Verify(row, Read(row), SecurityAuditTestKit.Integrity);
        row.Parameters = "{\"question\":\"\udc00\"}";

        intact.Should().Be(SecurityAuditVerdicts.Intact);
        McpAuditSeal.Verify(row, Read(row), SecurityAuditTestKit.Integrity).Should().Be(SecurityAuditVerdicts.Modified);
    }

    [Test]
    public async Task ARowIsSealed_WithWhatTheContentRetentionLockLeavesOfIt()
    {
        using var context = NpgsqlTestContext.Create();
        var row = Row();
        context.McpAuditLogs.Add(row);
        var retention = new ContentRetentionInterceptor(Mock.Of<IServiceProvider>(), NullLogger<ContentRetentionInterceptor>.Instance);

        await retention.RedactTrackedEntriesAsync(context.ChangeTracker, (_, _) => Task.FromResult(new ContentRetentionDecision(false, false)));
        Interceptor().Seal(context);

        row.Statements.Should().OnlyContain(x => x.Sql == null, "the lock strips the statement text before the seal");
        McpAuditSeal.Verify(row, Read(row), SecurityAuditTestKit.Integrity).Should().Be(SecurityAuditVerdicts.Intact);
    }

    [TestCase(EntityState.Modified)]
    [TestCase(EntityState.Deleted)]
    public void ASaveThatWouldChangeOrDeleteARowOrAStatement_IsRefused(EntityState state)
    {
        using var rowContext = NpgsqlTestContext.Create();
        var row = Row();
        row.Id = 5;
        row.Statements.Clear();
        rowContext.Attach(row);
        rowContext.Entry(row).State = state;
        using var statementContext = NpgsqlTestContext.Create();
        var statement = new McpAuditStatement { Id = 9, McpAuditLogId = 5, Purpose = McpStatementPurposes.Initial };
        statementContext.Attach(statement);
        statementContext.Entry(statement).State = state;

        var changeRow = () => Interceptor().Seal(rowContext);
        var changeStatement = () => Interceptor().Seal(statementContext);

        changeRow.Should().Throw<InvalidOperationException>().WithMessage("*append-only*");
        changeStatement.Should().Throw<InvalidOperationException>().WithMessage("*append-only*");
    }

    [Test]
    public void AStatementSavedWithoutItsRow_IsRefused()
    {
        using var context = NpgsqlTestContext.Create();
        context.McpAuditStatements.Add(new McpAuditStatement { McpAuditLogId = 5, Purpose = McpStatementPurposes.Initial });

        var act = () => Interceptor().Seal(context);

        act.Should().Throw<InvalidOperationException>().WithMessage("*append-only*");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void BothProviders_RunTheContentRetentionLock_BeforeTheSeal(bool sqlServer)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();

        var attached = (context.GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()!
            .Interceptors ?? []).ToList();

        var retention = attached.FindIndex(x => x is ContentRetentionInterceptor);
        var seal = attached.FindIndex(x => x is SecurityAuditInterceptor);
        retention.Should().BeGreaterThanOrEqualTo(0);
        seal.Should().BeGreaterThan(retention);
    }

    private static SecurityAuditInterceptor Interceptor()
    {
        return new SecurityAuditInterceptor(SecurityAuditTestKit.Integrity, NullLoggerFactory.Instance);
    }

    private static McpAuditLog SealedRow(SecurityAuditIntegrity? integrity = null)
    {
        var row = Row();
        McpAuditSeal.Seal(row, integrity ?? SecurityAuditTestKit.Integrity);

        return row;
    }

    // The statements as a read gives them back: separate objects, in their own list.
    private static List<McpAuditStatement> Read(McpAuditLog row)
    {
        return row.Statements
            .Select(x =>
                new McpAuditStatement
                {
                    CreatedTime = x.CreatedTime,
                    ProjectId = x.ProjectId,
                    DataSourceId = x.DataSourceId,
                    Purpose = x.Purpose,
                    SqlHash = x.SqlHash,
                    Sql = x.Sql,
                    TablesJson = x.TablesJson,
                    RowCount = x.RowCount,
                    DurationMs = x.DurationMs,
                    ErrorClass = x.ErrorClass
                })
            .ToList();
    }

    private static McpAuditLog Row()
    {
        return new McpAuditLog
        {
            UserId = 3,
            Tool = "query",
            Parameters = "{\"sql\":\"SELECT 1\"}",
            DataSourceId = 2,
            ProjectId = 42,
            ExecutionTimeMs = 15,
            ResultRowCount = 1,
            ErrorMessage = "timeout",
            CallerKind = "User",
            CallerHash = new string('c', 64),
            TraceId = new string('1', 32),
            SpanId = new string('2', 16),
            ApiKeyId = 8,
            ContentHash = new string('d', 64),
            Statements =
            [
                new McpAuditStatement { ProjectId = 42, DataSourceId = 2, Purpose = McpStatementPurposes.Initial, SqlHash = new string('e', 64), Sql = "SELECT 1", TablesJson = "[\"public.users\"]", RowCount = 1, DurationMs = 10 },
                new McpAuditStatement { ProjectId = 42, DataSourceId = 2, Purpose = McpStatementPurposes.Repair, SqlHash = new string('f', 64), Sql = "SELECT 1 FROM t", RowCount = 4, DurationMs = 5, ErrorClass = "timeout" }
            ]
        };
    }

    private static ServiceProvider BuildProvider(bool sqlServer)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Beacon:EncryptionKey"] = TestEncryptionKey.Value })
            .Build();
        var builder = services.AddBeaconServices(configuration, x => x.AddBeaconScheduler<NoOpScheduler>());
        if (sqlServer)
        {
            builder.UseSqlServer("Server=localhost;Database=unused;Trusted_Connection=True;TrustServerCertificate=True");
        }
        else
        {
            builder.UsePostgreSql("Host=localhost;Database=unused;Username=unused;Password=unused");
        }

        return services.BuildServiceProvider();
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
