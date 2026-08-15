using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.McpEval;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;

namespace Beacon.Tests.Unit;

/// <summary>
/// Part B — the <c>feedback</c> MCP tool. Verifies it routes the verdict to
/// <see cref="RecordQueryFeedbackCommand"/>, rejects an invalid verdict, and — the security-critical
/// guarantee (§1.11) — NEVER puts the user-supplied corrected SQL or note into the audit parameters or
/// the error slot. The audit is captured through a real <see cref="McpAuditService"/> over a mocked
/// context (no DB, §4.7); the corrected SQL/note contain PII sentinels that must not appear in the log.
/// </summary>
[TestFixture]
public class FeedbackToolTests
{
    private const int SignalId = 812;
    private const string PiiSql = "SELECT ssn FROM patients WHERE email = 'john@example.com'";
    private const string PiiNote = "wrong — should filter to patient 12345 (john@example.com)";

    [Test]
    public async Task Correct_SendsCommandWithVerdictAndUserText()
    {
        var mediator = new Mock<IMediator>();
        mediator.Setup(x => x.Send(It.IsAny<RecordQueryFeedbackCommand>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var (tool, _) = BuildTool(mediator);

        var result = await tool.ExecuteAsync(SignalId, "correct", PiiSql, PiiNote, CancellationToken.None);

        result.IsError.Should().NotBe(true);
        mediator.Verify(x => x.Send(
            It.Is<RecordQueryFeedbackCommand>(c =>
                c.SignalId == SignalId
                && c.Verdict == McpUserVerdict.Correct
                && c.CorrectedSql == PiiSql
                && c.Note == PiiNote),
            It.IsAny<CancellationToken>()), Times.Once,
            "the command carries the user text — persistence is fine; only logs/audit must exclude it");
    }

    [Test]
    public async Task InvalidVerdict_ReturnsError_NoSend_ButAudits()
    {
        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        var (tool, audit) = BuildTool(mediator);

        var result = await tool.ExecuteAsync(SignalId, "maybe", null, null, CancellationToken.None);

        result.IsError.Should().BeTrue();
        mediator.Verify(x => x.Send(It.IsAny<RecordQueryFeedbackCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        audit.Logs.Should().ContainSingle();
        audit.Logs[0].Tool.Should().Be("feedback");
        audit.Logs[0].ErrorMessage.Should().Contain("verdict");
    }

    [Test]
    public async Task DoesNotPutUserTextInAuditParametersOrError()
    {
        var mediator = new Mock<IMediator>();
        mediator.Setup(x => x.Send(It.IsAny<RecordQueryFeedbackCommand>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var (tool, audit) = BuildTool(mediator);

        await tool.ExecuteAsync(SignalId, "incorrect", PiiSql, PiiNote, CancellationToken.None);

        audit.Logs.Should().ContainSingle();
        var log = audit.Logs[0];

        // §1.11 — identifiers only.
        log.Parameters.Should().Contain("signal_id=812");
        log.Parameters.Should().Contain("verdict=incorrect");
        log.Parameters.Should().NotContain(PiiSql);
        log.Parameters.Should().NotContain(PiiNote);
        log.Parameters.Should().NotContain("john@example.com");
        (log.ErrorMessage ?? "").Should().NotContain("john@example.com");
    }

    private static (FeedbackTool Tool, CapturingAuditContext Audit) BuildTool(Mock<IMediator> mediator)
    {
        var auditContext = new CapturingAuditContext();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(auditContext);

        var auditService = new McpAuditService(factory.Object, NullLogger<McpAuditService>.Instance);
        var sessionManager = new McpProjectContextManager();

        var projectContext = new Mock<IProjectContext>();
        projectContext.SetupGet(x => x.UserId).Returns(1);
        projectContext.SetupGet(x => x.ActiveProjectId).Returns(5);
        projectContext.SetupGet(x => x.ApiKeyId).Returns((int?)null);

        var tool = new FeedbackTool(
            projectContext.Object,
            sessionManager,
            auditService,
            mediator.Object,
            NullLogger<FeedbackTool>.Instance);

        return (tool, auditContext);
    }

    /// <summary>Real McpAuditService writes McpAuditLog rows through this context; we capture them to assert
    /// no user text leaked into the audit trail.</summary>
    private sealed class CapturingAuditContext : BeaconContext
    {
        private static readonly DbContextOptions<CapturingAuditContext> Options =
            new DbContextOptionsBuilder<CapturingAuditContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly Mock<DbSet<McpAuditLog>> _set = new();

        public List<McpAuditLog> Logs { get; } = [];

        public CapturingAuditContext() : base(Options, "beacon")
        {
            _set.Setup(x => x.Add(It.IsAny<McpAuditLog>()))
                .Callback<McpAuditLog>(x => Logs.Add(x));
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpAuditLog))
            {
                return (DbSet<TEntity>)(object)_set.Object;
            }

            return base.Set<TEntity>();
        }

        public override int SaveChanges() => 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}
