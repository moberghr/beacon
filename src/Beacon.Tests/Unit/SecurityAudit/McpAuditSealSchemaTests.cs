using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NUnit.Framework;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Interceptors;
using Beacon.Core.Mcp;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Telemetry;
using Beacon.MCP.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.SecurityAudit;

/// <summary>
/// An MCP audit row is never lost on a schema the seal migration has not reached: the seal columns are found missing (the
/// table reads, they do not), and the row and its statements are written without them in one transaction, on either
/// provider; when the columns are there, a failed write stays a failed write.
/// </summary>
[TestFixture]
public class McpAuditSealSchemaTests
{
    [TestCase(false, "INSERT INTO beacon.mcp_audit_logs (", " RETURNING id AS \"Value\"", new[] { "hmac", "audit_guid", "seal_version" })]
    [TestCase(true, "INSERT INTO [beacon].[McpAuditLogs] (", " OUTPUT INSERTED.[Id] AS [Value] VALUES (", new[] { "[Hmac]", "[AuditGuid]", "[SealVersion]", "[HmacKeyId]" })]
    public void TheRowInsertWithoutTheSeal_Translates(bool sqlServer, string start, string returning, string[] absent)
    {
        using var context = sqlServer ? (Core.Data.BeaconContext)SqlServerTestContext.Create() : NpgsqlTestContext.Create();
        var row = SealedRow();

        var (sql, parameters) = McpAuditSealSchema.Insert(context, row, null);

        sql.Should().StartWith(start).And.Contain(returning).And.NotContainAny(absent);
        parameters.Should().HaveCount(sql.Count(x => x == '@'));
        parameters.Cast<System.Data.Common.DbParameter>().Select(x => x.Value).Should().Contain("query");
    }

    [TestCase(false, "INSERT INTO beacon.mcp_audit_statements (", "mcp_audit_log_id")]
    [TestCase(true, "INSERT INTO [beacon].[McpAuditStatements] (", "[McpAuditLogId]")]
    public void TheStatementInsert_NamesItsRow_Translates(bool sqlServer, string start, string foreignKey)
    {
        using var context = sqlServer ? (Core.Data.BeaconContext)SqlServerTestContext.Create() : NpgsqlTestContext.Create();

        var (sql, parameters) = McpAuditSealSchema.Insert(context, SealedRow().Statements[0], 7);

        sql.Should().StartWith(start).And.Contain(foreignKey).And.NotContain("RETURNING").And.NotContain("OUTPUT");
        parameters.Cast<System.Data.Common.DbParameter>().Select(x => x.Value).Should().Contain(7).And.Contain("SELECT 1");
    }

    [Test]
    public async Task TheSealColumns_AreMissing_WhenTheTableReadsAndTheyDoNot()
    {
        var present = new SqlCapture().ThenNoRows().ThenNoRows();
        var missing = new SqlCapture().ThenNoRows().ThenThrow(UndefinedColumn());
        var noTable = new SqlCapture().ThenThrow(new PostgresException("relation does not exist", "ERROR", "ERROR", "42P01"));

        (await new McpAuditSealSchema(present.Factory()).SealColumnsMissingAsync(CancellationToken.None)).Should().BeFalse();
        (await new McpAuditSealSchema(missing.Factory()).SealColumnsMissingAsync(CancellationToken.None)).Should().BeTrue();
        (await new McpAuditSealSchema(noTable.Factory()).SealColumnsMissingAsync(CancellationToken.None)).Should().BeNull();
        missing.Commands[1].Should().Contain("hmac");
    }

    [Test]
    public async Task ARowWrittenWithoutItsSeal_KeepsItsStatements_InOneTransaction()
    {
        var capture = new SqlCapture().ThenScalar(7).ThenRowsAffected(1);
        var row = SealedRow();

        var id = await new McpAuditSealSchema(capture.Factory()).WriteWithoutSealAsync(row, CancellationToken.None);

        id.Should().Be(7);
        capture.Timeline.Should().HaveCount(4);
        capture.Timeline[0].Should().Be(SqlCapture.TransactionBegun);
        capture.Timeline[1].Should().StartWith("INSERT INTO beacon.mcp_audit_logs").And.NotContain("hmac");
        capture.Timeline[2].Should().StartWith("INSERT INTO beacon.mcp_audit_statements");
        capture.Timeline[3].Should().Be(SqlCapture.TransactionCommitted);
        capture.CommandParameters[1].Values.Should().Contain(7);
    }

    [Test]
    public async Task AnAuditWrite_OnASchemaWithoutTheSealColumns_IsWrittenWithoutTheSeal_NeverLost()
    {
        var capture = new SqlCapture()
            .ThenThrow(UndefinedColumn())
            .ThenNoRows()
            .ThenThrow(UndefinedColumn())
            .ThenScalar(7)
            .ThenRowsAffected(1);
        var outcome = new McpAuditOutcome();

        await Service(capture, outcome).LogToolCallAsync(1, "query", "{}", 2, 3, 10, 1, null, ct: CancellationToken.None);

        outcome.Should().BeEquivalentTo(new { Written = true, Failed = false });
        capture.Commands.Should().HaveCount(5);
        capture.Commands[3].Should().StartWith("INSERT INTO beacon.mcp_audit_logs").And.NotContain("hmac");
    }

    [Test]
    public async Task AFailedAuditWrite_WithTheSealColumnsInPlace_StaysFailed()
    {
        var capture = new SqlCapture()
            .ThenThrow(new PostgresException("duplicate key", "ERROR", "ERROR", "23505"))
            .ThenNoRows()
            .ThenNoRows();
        var outcome = new McpAuditOutcome();

        await Service(capture, outcome).LogToolCallAsync(1, "query", "{}", 2, 3, 10, 1, null, ct: CancellationToken.None);

        outcome.Should().BeEquivalentTo(new { Written = false, Failed = true });
        capture.Commands.Should().HaveCount(3, "the failed write, then the two reads that find the columns there");
    }

    private static McpAuditService Service(SqlCapture capture, McpAuditOutcome outcome)
    {
        var factory = capture.Factory(new SecurityAuditInterceptor(SecurityAuditTestKit.Integrity, NullLoggerFactory.Instance));
        var statementLog = new McpStatementLog();
        statementLog.Record(new McpStatement(2, McpStatementPurposes.Initial, "SELECT 1", ["orders"], 1, 5, null));

        return new McpAuditService(
            factory,
            SettingsProviderMock.Create().Object,
            new HttpContextAccessor(),
            Options.Create(new McpDeploymentOptions()),
            NullLogger<McpAuditService>.Instance,
            outcome,
            Options.Create(new BeaconTelemetryOptions()),
            NullLoggerFactory.Instance,
            TestMcpAudit.ContentHasher,
            statementLog,
            new McpAuditSealSchema(factory));
    }

    private static PostgresException UndefinedColumn()
    {
        return new PostgresException("column does not exist", "ERROR", "ERROR", "42703");
    }

    private static McpAuditLog SealedRow()
    {
        var row = new McpAuditLog
        {
            CreatedTime = new DateTime(2026, 10, 24, 9, 0, 0, DateTimeKind.Utc),
            UserId = 3,
            Tool = "query",
            ProjectId = 42,
            ExecutionTimeMs = 10,
            Statements =
            [
                new McpAuditStatement { CreatedTime = new DateTime(2026, 10, 24, 9, 0, 0, DateTimeKind.Utc), ProjectId = 42, Purpose = McpStatementPurposes.Initial, Sql = "SELECT 1", RowCount = 1 }
            ]
        };
        McpAuditSeal.Seal(row, SecurityAuditTestKit.Integrity);

        return row;
    }
}
