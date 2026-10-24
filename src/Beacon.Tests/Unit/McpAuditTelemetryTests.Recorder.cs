using System.Data.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Moq;
using NUnit.Framework;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Mcp;
using Beacon.Core.Models;
using Beacon.Core.Services.Retention;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// <see cref="McpToolCallRecorder"/> written by <see cref="McpAuditCallToolFilter"/> (§1.7, §9.5): a call a tool records is
/// written once, after the tool returned or threw, the audit row before the filter answers and then the signal of a
/// SQL-carrying tool; a tool that still writes its own row is never written twice; a tool that throws without any row is
/// audited all the same; and under the required audit a recorded call whose row cannot be written is withheld.
/// </summary>
public partial class McpAuditTelemetryTests
{
    private const string RecordedSql = "SELECT id FROM customers";

    [Test]
    public async Task RecordedCall_IsWrittenAfterTheTool_OneRowWithTheRecordedFields()
    {
        var harness = new RecorderHarness();

        var result = await harness.InvokeAsync("query", recorder =>
        {
            recorder.Start("query", MatrixUserId, RecordedSql)
                .WithProject(MatrixProjectId)
                .WithDataSource(MatrixDataSourceId)
                .WithSignal(new McpSignalBuilder().SetTool("query").SetUserId(MatrixUserId), RecordedSql);
            harness.AuditRows.Should().BeEmpty("nothing is written while the tool runs");
            recorder.Succeed(3, ["customers"]);

            return ToolHelper.Success("3 rows");
        });

        result.IsError.Should().NotBe(true);
        var row = harness.AuditRows.Should().ContainSingle().Subject;
        row.Tool.Should().Be("query");
        row.UserId.Should().Be(MatrixUserId);
        row.Parameters.Should().NotBeNull();
        row.ProjectId.Should().Be(MatrixProjectId);
        row.DataSourceId.Should().Be(MatrixDataSourceId);
        row.ResultRowCount.Should().Be(3);
        row.ErrorMessage.Should().BeNull();
        var signal = harness.Signals.Should().ContainSingle().Subject;
        signal.Tool.Should().Be("query");
        signal.IsSuccessful.Should().BeTrue();
        signal.ResultRowCount.Should().Be(3);
        harness.Outcome.Written.Should().BeTrue();
    }

    [Test]
    public async Task RecordedRefusal_WritesTheErrorAndAFailedSignal()
    {
        var harness = new RecorderHarness();

        await harness.InvokeAsync("dry_run", recorder =>
        {
            recorder.Start("dry_run", MatrixUserId, RecordedSql)
                .WithSignal(new McpSignalBuilder().SetTool("dry_run"), RecordedSql);
            recorder.Fail("Access denied: you do not have access to project 43.");

            return ToolHelper.Error("Access denied: you do not have access to project 43.");
        });

        harness.AuditRows.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be("Access denied: you do not have access to project 43.");
        var signal = harness.Signals.Should().ContainSingle().Subject;
        signal.IsSuccessful.Should().BeFalse();
        signal.ExecutionFailed.Should().BeTrue();
    }

    [Test]
    public async Task RecordedSignalVerdict_CanDifferFromTheCallsOutcome()
    {
        var harness = new RecorderHarness();

        await harness.InvokeAsync("dry_run", recorder =>
        {
            recorder.Start("dry_run", MatrixUserId, RecordedSql)
                .WithSignal(new McpSignalBuilder().SetTool("dry_run"), RecordedSql);
            recorder.Succeed(signalSucceeded: false);

            return ToolHelper.Success("INVALID");
        });

        harness.AuditRows.Should().ContainSingle().Which.ErrorMessage.Should().BeNull();
        harness.Signals.Should().ContainSingle().Which.IsSuccessful.Should().BeFalse();
    }

    [Test]
    public async Task ToolThrowsAfterStartingItsRecord_IsAuditedAndAnswersTheCallerSafeError()
    {
        var harness = new RecorderHarness();

        var result = await harness.InvokeAsync("ask", recorder =>
        {
            recorder.Start("ask", MatrixUserId, MatrixQuestion)
                .WithProject(MatrixProjectId)
                .WithSignal(new McpSignalBuilder().SetTool("ask"));

            throw new InvalidOperationException(DependencyFailure);
        });

        result.IsError.Should().BeTrue();
        TextOf(result).Should().Be(DependencyFailure);
        var row = harness.AuditRows.Should().ContainSingle().Subject;
        row.Tool.Should().Be("ask");
        row.ProjectId.Should().Be(MatrixProjectId);
        row.ErrorMessage.Should().Be(DependencyFailure);
        var signal = harness.Signals.Should().ContainSingle().Subject;
        signal.IsSuccessful.Should().BeFalse();
        signal.ExecutionFailed.Should().BeTrue();
    }

    [Test]
    public async Task ToolThrowsAnUnexpectedError_TheCallerGetsTheGenericMessage()
    {
        var harness = new RecorderHarness();

        var result = await harness.InvokeAsync("search", recorder =>
        {
            recorder.Start("search", MatrixUserId, "customers");

            throw new NotSupportedException("internal detail");
        });

        TextOf(result).Should().Be(ToolHelper.CallerSafeMessage(new NotSupportedException("internal detail"), "search"));
        TextOf(result).Should().NotContain("internal detail");
        harness.AuditRows.Should().ContainSingle().Which.ErrorMessage.Should().Be("internal detail");
        harness.Signals.Should().BeEmpty("search is audit-only");
    }

    [Test]
    public async Task ToolReturnsWithoutAnOutcome_IsRecordedFromItsResult()
    {
        var harness = new RecorderHarness();

        await harness.InvokeAsync("get_context", recorder =>
        {
            recorder.Start("get_context", MatrixUserId);

            return ToolHelper.Error("Project 42 not found.");
        });

        harness.AuditRows.Should().ContainSingle().Which.ErrorMessage.Should().Be("Project 42 not found.");
    }

    [Test]
    public async Task ToolWritesItsOwnRowAndStartsARecord_GetsOneRow()
    {
        var harness = new RecorderHarness();

        await harness.InvokeAsync("search", (recorder, services) =>
        {
            recorder.Start("search", MatrixUserId, "customers");
            recorder.Succeed(1);
            services.GetRequiredService<McpAuditService>()
                .LogToolCallAsync(MatrixUserId, "search", "customers", null, MatrixProjectId, 0, 1, null)
                .GetAwaiter()
                .GetResult();

            return ToolHelper.Success("1 result");
        });

        harness.AuditRows.Should().ContainSingle();
    }

    [Test]
    public async Task ToolThrowsWithoutARecordOrARow_IsAuditedOnce_AndTheExceptionPropagates()
    {
        var harness = new RecorderHarness();

        var act = () => harness.InvokeAsync("feedback", _ => throw new TimeoutException("store timed out"));

        await act.Should().ThrowAsync<TimeoutException>();
        var row = harness.AuditRows.Should().ContainSingle().Subject;
        row.Tool.Should().Be("feedback");
        row.UserId.Should().Be(MatrixUserId);
        row.ProjectId.Should().BeNull();
        row.ErrorMessage.Should().Be(McpContentRedactor.ErrorClassOf("store timed out"), "without a known project no project's retention can keep the text");
    }

    [Test]
    public async Task UnrecordedFailureInAKnownProject_FollowsThatProjectsRetention()
    {
        var harness = new RecorderHarness(projectSettings: new McpSettingsData { RetainQueryContent = false });
        harness.ProjectContext.ActiveProjectId = MatrixProjectId;

        var act = () => harness.InvokeAsync("search", _ => throw new TimeoutException("store timed out on customers"));

        await act.Should().ThrowAsync<TimeoutException>();
        var row = harness.AuditRows.Should().ContainSingle().Subject;
        row.ProjectId.Should().Be(MatrixProjectId);
        row.ErrorMessage.Should().Be(McpContentRedactor.ErrorClassOf("store timed out on customers"));
    }

    [Test]
    public async Task UnrecordedFailureInAProjectThatRetainsContent_KeepsTheMessage()
    {
        var harness = new RecorderHarness(projectSettings: new McpSettingsData { RetainQueryContent = true });
        harness.ProjectContext.ActiveProjectId = MatrixProjectId;

        var act = () => harness.InvokeAsync("search", _ => throw new TimeoutException("store timed out"));

        await act.Should().ThrowAsync<TimeoutException>();
        harness.AuditRows.Should().ContainSingle().Which.ErrorMessage.Should().Be("store timed out");
    }

    [Test]
    public async Task RecordedCallThrowingAProtocolError_IsWrittenOnce_AndTheErrorPropagates()
    {
        var harness = new RecorderHarness();

        var act = () => harness.InvokeAsync("query", recorder =>
        {
            recorder.Start("query", MatrixUserId, RecordedSql)
                .WithProject(MatrixProjectId)
                .WithSignal(new McpSignalBuilder().SetTool("query"), RecordedSql);

            throw new McpProtocolException("Missing argument: sql", McpErrorCode.InvalidParams);
        });

        await act.Should().ThrowAsync<McpProtocolException>();
        var row = harness.AuditRows.Should().ContainSingle().Subject;
        row.Tool.Should().Be("query");
        row.ErrorMessage.Should().Be("Missing argument: sql");
        harness.Signals.Should().ContainSingle().Which.IsSuccessful.Should().BeFalse();
    }

    [Test]
    public async Task RecordedCancellation_IsWrittenOnce_WithTheToolsOutcome_AndTheCancellationPropagates()
    {
        var harness = new RecorderHarness();

        var act = () => harness.InvokeAsync("ask", recorder =>
        {
            recorder.Start("ask", MatrixUserId, MatrixQuestion)
                .WithProject(MatrixProjectId)
                .WithSignal(new McpSignalBuilder().SetTool("ask"));
            recorder.FailCancelled("Cancelled by the caller.");

            throw new OperationCanceledException();
        });

        await act.Should().ThrowAsync<OperationCanceledException>();
        var row = harness.AuditRows.Should().ContainSingle().Subject;
        row.Tool.Should().Be("ask");
        row.ProjectId.Should().Be(MatrixProjectId);
        row.ErrorMessage.Should().Be("Cancelled by the caller.");
        var signal = harness.Signals.Should().ContainSingle().Subject;
        signal.IsSuccessful.Should().BeFalse();
        signal.ExecutionFailed.Should().BeTrue();
    }

    [Test]
    public async Task RecordedAnswerWithSignalId_IsRebuiltWithThePersistedSignalsId()
    {
        var harness = new RecorderHarness();

        var result = await harness.InvokeAsync("ask", recorder =>
        {
            recorder.Start("ask", MatrixUserId, MatrixQuestion)
                .WithProject(MatrixProjectId)
                .WithSignal(new McpSignalBuilder().SetTool("ask").SetProjectId(MatrixProjectId));
            recorder.Succeed(2);

            return recorder.AnswerWithSignalId(x => ToolHelper.Success(x is { } id ? $"answer, signal {id}" : "answer"));
        });

        var signal = harness.Signals.Should().ContainSingle().Subject;
        result.IsError.Should().NotBe(true);
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Be($"answer, signal {signal.Id}");
        harness.AuditRows.Should().ContainSingle().Which.ResultRowCount.Should().Be(2);
    }

    [Test]
    public async Task RecordedFailure_KeepsTheToolsAnswer_WhateverItsSignalId()
    {
        var harness = new RecorderHarness();

        var result = await harness.InvokeAsync("ask", recorder =>
        {
            recorder.Start("ask", MatrixUserId, MatrixQuestion)
                .WithProject(MatrixProjectId)
                .WithSignal(new McpSignalBuilder().SetTool("ask").SetProjectId(MatrixProjectId));
            recorder.AnswerWithSignalId(_ => ToolHelper.Success("answer"));
            recorder.Fail("This project has no data sources configured.");

            return ToolHelper.Error("This project has no data sources configured.");
        });

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Be("This project has no data sources configured.");
        harness.Signals.Should().ContainSingle().Which.IsSuccessful.Should().BeFalse();
    }

    [Test]
    public async Task UnknownToolOnTheTransport_IsAuditedOnce_AsThePlaygroundAuditsIt()
    {
        var harness = new RecorderHarness();
        harness.ProjectContext.ActiveProjectId = MatrixProjectId;

        var act = () => harness.InvokeAsync("not_a_tool", _ => throw new McpProtocolException("Unknown tool: 'not_a_tool'", McpErrorCode.InvalidParams));

        await act.Should().ThrowAsync<McpProtocolException>();
        var row = harness.AuditRows.Should().ContainSingle().Subject;
        row.Tool.Should().Be("not_a_tool");
        row.ErrorMessage.Should().Be("Unknown tool: 'not_a_tool'");
    }

    [Test]
    public async Task ToolsOwnRowFailedToWrite_ThenThrows_TheRowIsWrittenAgain()
    {
        var harness = new RecorderHarness(auditRequired: true);

        var act = () => harness.InvokeAsync("ask", (_, services) =>
        {
            services.GetRequiredService<McpAuditOutcome>().Failed = true;

            throw new TimeoutException("store timed out");
        });

        await act.Should().ThrowAsync<TimeoutException>();
        harness.AuditRows.Should().ContainSingle();
        harness.Outcome.Written.Should().BeTrue();
        harness.Outcome.Failed.Should().BeFalse("the retried row is written, so the call is audited");
    }

    [Test]
    public async Task ToolsOwnRowFailedToWrite_TheRecordIsWritten_AndTheResultIsNotWithheld()
    {
        var harness = new RecorderHarness(auditRequired: true);

        var result = await harness.InvokeAsync("query", (recorder, services) =>
        {
            recorder.Start("query", MatrixUserId, RecordedSql)
                .WithSignal(new McpSignalBuilder().SetTool("query"), RecordedSql);
            services.GetRequiredService<McpAuditOutcome>().Failed = true;
            recorder.Succeed(3);

            return ToolHelper.Success("3 rows");
        });

        result.IsError.Should().NotBe(true, TextOf(result));
        harness.AuditRows.Should().ContainSingle().Which.ResultRowCount.Should().Be(3);
        harness.Signals.Should().ContainSingle();
    }

    [Test]
    public async Task ToolsOwnRowWritten_TheRecordedSignalIsStillWritten()
    {
        var harness = new RecorderHarness();

        await harness.InvokeAsync("dry_run", (recorder, services) =>
        {
            recorder.Start("dry_run", MatrixUserId, RecordedSql)
                .WithSignal(new McpSignalBuilder().SetTool("dry_run"), RecordedSql);
            services.GetRequiredService<McpAuditOutcome>().Written = true;
            recorder.Succeed();

            return ToolHelper.Success("VALID");
        });

        harness.AuditRows.Should().BeEmpty("the tool's own row was written (directly, outside this test's capture)");
        harness.Signals.Should().ContainSingle().Which.Tool.Should().Be("dry_run");
    }

    [Test]
    public async Task ToolWritingItsRecordWhileRunning_IsRefused_AndTheRowRecordsTheFailure()
    {
        var harness = new RecorderHarness();

        var result = await harness.InvokeAsync("ask", recorder =>
        {
            recorder.Start("ask", MatrixUserId, MatrixQuestion)
                .WithSignal(new McpSignalBuilder().SetTool("ask"));
            recorder.Succeed();
            recorder.WriteAsync(CancellationToken.None).GetAwaiter().GetResult();

            return ToolHelper.Success("answered");
        });

        result.IsError.Should().BeTrue();
        var row = harness.AuditRows.Should().ContainSingle().Subject;
        row.ErrorMessage.Should().NotBeNull("the row holds the call's final outcome, never an early success");
        harness.Signals.Should().ContainSingle().Which.IsSuccessful.Should().BeFalse();
    }

    [Test]
    public async Task RecordedDatabaseError_ReachesTheCallerRedacted()
    {
        var harness = new RecorderHarness();

        var result = await harness.InvokeAsync("query", recorder =>
        {
            recorder.Start("query", MatrixUserId, RecordedSql).WithProject(MatrixProjectId);

            throw new MatrixDbException("invalid input syntax for type integer: \"4111-1111-1111-1111\"");
        });

        TextOf(result).Should().StartWith("Query failed: ");
        TextOf(result).Should().NotContain("4111-1111-1111-1111");
    }

    [Test]
    public async Task RecordedDatabaseError_WithPiiDetectionOff_KeepsTheDatabaseText()
    {
        var harness = new RecorderHarness(projectSettings: new McpSettingsData { EnablePiiDetection = false }, forcePiiDetection: false);

        var result = await harness.InvokeAsync("query", recorder =>
        {
            recorder.Start("query", MatrixUserId, RecordedSql).WithProject(MatrixProjectId);

            throw new MatrixDbException("relation \"orders\" does not exist");
        });

        TextOf(result).Should().Be("Query failed: relation \"orders\" does not exist");
    }

    [Test]
    public async Task ToolThrowsAfterWritingItsOwnRow_GetsNoSecondRow()
    {
        var harness = new RecorderHarness();

        var act = () => harness.InvokeAsync("ask", (_, services) =>
        {
            services.GetRequiredService<McpAuditService>()
                .LogToolCallAsync(MatrixUserId, "ask", MatrixQuestion, null, MatrixProjectId, 0, null, "cancelled")
                .GetAwaiter()
                .GetResult();

            throw new OperationCanceledException();
        });

        await act.Should().ThrowAsync<OperationCanceledException>();
        harness.AuditRows.Should().ContainSingle().Which.ErrorMessage.Should().Be("cancelled");
    }

    [Test]
    public async Task AuditRequiredAndTheRecordedRowCannotBeWritten_TheResultIsWithheld()
    {
        var harness = new RecorderHarness(auditRequired: true, auditSaveThrows: true);

        var result = await harness.InvokeAsync("query", recorder =>
        {
            recorder.Start("query", MatrixUserId, RecordedSql);
            recorder.Succeed(3);

            return ToolHelper.Success("3 rows");
        });

        result.IsError.Should().BeTrue();
        TextOf(result).Should().Be(McpAuditCallToolFilter.WithheldMessage);
        harness.Outcome.Failed.Should().BeTrue();
    }

    [TestCase("get_context")]
    [TestCase("search")]
    [TestCase("get_documentation")]
    [TestCase("get_query_context")]
    [TestCase("feedback")]
    public void AuditOnlyTool_CannotAttachASignal(string tool)
    {
        var recorder = new RecorderHarness().Recorder();
        recorder.Start(tool, MatrixUserId);

        var act = () => recorder.WithSignal(new McpSignalBuilder().SetTool(tool));

        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void SignalTools_AreTheSqlCarryingTools()
    {
        McpToolCallRecorder.SignalTools.Should().BeEquivalentTo("ask", "query", "dry_run");
    }

    [Test]
    public void AddBeaconMcp_RegistersOneRecorderPerScope()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        Beacon.MCP.ServiceConfiguration.AddBeaconMcp(services);

        services.Should().ContainSingle(x => x.ServiceType == typeof(McpToolCallRecorder))
            .Which.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    private sealed class MatrixDbException(string message) : DbException(message);

    /// <summary>A scope with the real audit and signal services over recording contexts, and the recorder.</summary>
    private sealed class RecorderHarness
    {
        private readonly ServiceProvider _provider;

        public RecorderHarness(
            bool auditRequired = false,
            bool auditSaveThrows = false,
            McpSettingsData? projectSettings = null,
            bool forcePiiDetection = true)
        {
            var signalFactory = new Mock<IDbContextFactory<BeaconContext>>();
            signalFactory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new ToolMatrixContext(Signals));
            var auditFactory = new Mock<IDbContextFactory<BeaconContext>>();
            auditFactory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new ToolMatrixContext(Signals, AuditRows, auditSaveThrows));
            var deployment = Options.Create(new McpDeploymentOptions
            {
                Audit = new McpAuditOptions { Required = auditRequired },
                ForcePiiDetection = forcePiiDetection
            });
            var settings = SettingsProviderMock.Create(
                projectSettings: projectSettings == null ? null : new Dictionary<int, McpSettingsData> { [MatrixProjectId] = projectSettings });

            var services = new ServiceCollection();
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddSingleton(deployment);
            services.AddScoped(_ => new McpProjectContext { UserId = MatrixUserId, AllowedProjectIds = [MatrixProjectId] });
            services.AddScoped<IProjectContext>(x => x.GetRequiredService<McpProjectContext>());
            services.AddScoped<McpAuditOutcome>();
            services.AddScoped<IMcpStatementLog, McpStatementLog>();
            services.AddSingleton(settings.Object);
            services.AddScoped(x => new McpAuditService(
                auditFactory.Object,
                settings.Object,
                new HttpContextAccessor(),
                deployment,
                NullLogger<McpAuditService>.Instance,
                x.GetRequiredService<McpAuditOutcome>(),
                Options.Create(new BeaconTelemetryOptions()),
                NullLoggerFactory.Instance,
                TestMcpAudit.ContentHasher,
                x.GetRequiredService<IMcpStatementLog>()));
            services.AddScoped(_ => new McpSignalService(signalFactory.Object, SettingsProviderMock.Create().Object, NullLogger<McpSignalService>.Instance));
            services.AddScoped<McpToolCallRecorder>();
            _provider = services.BuildServiceProvider();
            Scope = _provider.CreateScope();
        }

        public List<McpAuditLog> AuditRows { get; } = [];

        public List<McpQuerySignal> Signals { get; } = [];

        public IServiceScope Scope { get; }

        public McpAuditOutcome Outcome => Scope.ServiceProvider.GetRequiredService<McpAuditOutcome>();

        public McpProjectContext ProjectContext => Scope.ServiceProvider.GetRequiredService<McpProjectContext>();

        public McpToolCallRecorder Recorder() => Scope.ServiceProvider.GetRequiredService<McpToolCallRecorder>();

        public Task<CallToolResult> InvokeAsync(string tool, Func<McpToolCallRecorder, CallToolResult> run) =>
            InvokeAsync(tool, (recorder, _) => run(recorder));

        public Task<CallToolResult> InvokeAsync(string tool, Func<McpToolCallRecorder, IServiceProvider, CallToolResult> run)
        {
            var services = Scope.ServiceProvider;

            return McpAuditCallToolFilter.InvokeAsync(
                services,
                tool,
                _ => ValueTask.FromResult(run(services.GetRequiredService<McpToolCallRecorder>(), services)),
                CancellationToken.None);
        }
    }
}
