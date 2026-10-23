using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Exceptions;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ExecutionLog;

/// <summary>
/// The execution log of the statements Beacon runs for its own features: <see cref="GatedSqlRunner"/> writes a Started row
/// before a statement that passed the gate is sent and closes it with how the statement ended; <see cref="SqlExecutionAudit"/>
/// writes and closes the row, attributed to the scope's caller by <c>Users.Id</c>, and refuses to let the statement run
/// without its row only when the log is required. No database (§4.7): a recording log, a recording provider, captured SQL.
/// </summary>
[TestFixture]
public class SqlExecutionLogTests
{
    private static readonly DataSource Source = new()
    {
        Id = 3,
        Name = "core",
        DataSourceType = DataSourceType.Database,
        DatabaseEngineType = DatabaseEngineType.PostgreSQL,
        EncryptedConnectionData = "encrypted"
    };

    [Test]
    public async Task AStatement_IsLoggedBeforeItIsSent_AndClosedWithItsRows()
    {
        var order = new List<string>();
        var audit = new RecordingExecutionAudit(order);
        var provider = Answering(new ProviderQueryResult { Success = true, Rows = [new() { ["id"] = 1 }, new() { ["id"] = 2 }], ExecutionTimeMs = 4 }, order);
        var runner = Runner(provider.Object, audit);

        var run = await runner.RunAsync(Request("SELECT id FROM accounts", SqlExecutionContexts.SubscriptionStep), CancellationToken.None);

        order.Should().Equal("start", "execute", "end");
        audit.Starts.Should().ContainSingle()
            .Which.Should().Be(new SqlExecutionStart(Source.Id, run.Report.FinalSql, SqlExecutionContexts.SubscriptionStep));
        audit.Ends.Should().ContainSingle()
            .Which.Should().Be(new SqlExecutionEnd(QueryExecutionLogStatus.Succeeded, 2, 4));
    }

    [Test]
    public async Task AStatementWithoutAContext_IsLoggedAsAQueryStep()
    {
        var audit = new RecordingExecutionAudit();
        var runner = Runner(Answering(new ProviderQueryResult { Success = true }).Object, audit);

        await runner.RunAsync(Request("SELECT id FROM accounts"), CancellationToken.None);

        audit.Starts.Should().ContainSingle().Which.ExecutionContext.Should().Be(SqlExecutionContexts.QueryStep);
    }

    [Test]
    public async Task AFailedStatement_IsClosedFailed_WithWhatTheCallerWasTold()
    {
        var audit = new RecordingExecutionAudit();
        var runner = Runner(Answering(new ProviderQueryResult { Success = false, ErrorMessage = "relation \"accounts\" does not exist" }).Object, audit);

        var act = () => runner.RunAsync(Request("SELECT id FROM accounts"), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<QueryExecutionFailedException>();
        audit.Ends.Should().ContainSingle()
            .Which.Should().Match<SqlExecutionEnd>(x => x.Status == QueryExecutionLogStatus.Failed && x.ErrorMessage == thrown.Which.Message);
    }

    [Test]
    public async Task AStatementTheCallerCancels_IsClosedCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var audit = new RecordingExecutionAudit();
        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .Returns(async (DataSource _, string _, Dictionary<string, object?> _, CancellationToken token) =>
            {
                await cancellation.CancelAsync();
                token.ThrowIfCancellationRequested();

                return new ProviderQueryResult { Success = true };
            });
        var runner = Runner(provider.Object, audit);

        var act = () => runner.RunAsync(Request("SELECT id FROM accounts"), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        audit.Ends.Should().ContainSingle().Which.Status.Should().Be(QueryExecutionLogStatus.Cancelled);
    }

    [Test]
    public async Task AStatementPastItsTimeout_IsClosedTimedOut()
    {
        var audit = new RecordingExecutionAudit();
        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .Returns(async (DataSource _, string _, Dictionary<string, object?> _, CancellationToken token) =>
            {
                await Task.Delay(Timeout.Infinite, token);

                return new ProviderQueryResult { Success = true };
            });
        var runner = new GatedSqlRunner(TestSqlGate.Create(), TestQueryServices.Providers(provider.Object), TestConfiguredDatabases.None, TestQueryServices.QueryOptions, executionAudit: audit)
        {
            StatementTimeout = TimeSpan.FromMilliseconds(50)
        };

        var run = await runner.RunAsync(Request("SELECT id FROM accounts"), CancellationToken.None);

        run.TimedOut.Should().BeTrue();
        audit.Ends.Should().ContainSingle().Which.Status.Should().Be(QueryExecutionLogStatus.TimedOut);
    }

    [Test]
    public async Task ARefusedStatement_IsNotLogged()
    {
        var audit = new RecordingExecutionAudit();
        var runner = Runner(new Mock<IDataSourceProvider>(MockBehavior.Strict).Object, audit);

        var act = () => runner.RunAsync(Request("UPDATE accounts SET balance = 0"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        audit.Starts.Should().BeEmpty("nothing reached the data source");
    }

    [Test]
    public async Task WhenTheLogIsRequired_AStatementWhoseRowCannotBeWritten_DoesNotRun()
    {
        var provider = new Mock<IDataSourceProvider>(MockBehavior.Strict);
        var runner = Runner(provider.Object, Audit(Unavailable(), required: true, new LogRecorder()));

        var act = () => runner.RunAsync(Request("SELECT id FROM accounts"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*execution log*");
        provider.Invocations.Should().BeEmpty();
    }

    [Test]
    public async Task ByDefault_AStatementWhoseRowCannotBeWritten_RunsWithAWarning()
    {
        var logs = new LogRecorder();
        var provider = Answering(new ProviderQueryResult { Success = true, Rows = [new() { ["id"] = 1 }] });
        var runner = Runner(provider.Object, Audit(Unavailable(), required: false, logs));

        var run = await runner.RunAsync(Request("SELECT id FROM accounts"), CancellationToken.None);

        run.Rows.Should().HaveCount(1);
        logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("Beacon:Audit:ExecutionLog:Required");
    }

    [Test]
    public async Task TheStartedRow_RecordsTheStatement_AndTheCallerByUsersId()
    {
        var saved = new List<object>();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(new Dictionary<Type, object>(), saved));
        var logs = new LogRecorder();
        var audit = Audit(factory.Object, required: true, logs, new BeaconActor("ext-ana", false) { BeaconUserId = 5 });

        await audit.StartAsync(new SqlExecutionStart(Source.Id, "SELECT id FROM accounts LIMIT 11", SqlExecutionContexts.QueryStep), CancellationToken.None);

        logs.Entries.Should().BeEmpty();
        var row = saved.Should().ContainSingle().Which.Should().BeOfType<ManualQueryExecutionLog>().Subject;
        row.TriggeredByUserId.Should().Be(5);
        row.UserId.Should().BeNull("the caller is recorded by Users.Id only");
        row.QueryText.Should().Be("SELECT id FROM accounts LIMIT 11");
        row.DataSourceId.Should().Be(Source.Id);
        row.ExecutionContext.Should().Be(SqlExecutionContexts.QueryStep);
        row.Status.Should().Be(QueryExecutionLogStatus.Started);
        row.Success.Should().BeFalse();
        row.CompletedTime.Should().BeNull();
    }

    [Test]
    public async Task TheEnd_ClosesOnlyAStartedRow_WhateverTheCallersCancellation()
    {
        var capture = new SqlCapture().ThenRowsAffected(1);
        var audit = Audit(capture.Factory(), required: false, new LogRecorder());

        await audit.CompleteAsync(new SqlExecutionEntry(42), new SqlExecutionEnd(QueryExecutionLogStatus.Failed, 0, 3, new string('x', 5000)));

        var update = capture.Commands.Should().ContainSingle().Which;
        update.Should().StartWith("UPDATE beacon.manual_query_execution_logs");
        update.Should().Contain("status = 1", "a row already closed is never rewritten");
        capture.CommandParameters.Single().Values.OfType<string>().Should().ContainSingle()
            .Which.Length.Should().Be(SqlExecutionAudit.MaxErrorMessageLength);
    }

    [Test]
    public async Task ARowThatWasNotWritten_IsNotClosed()
    {
        var capture = new SqlCapture();
        var audit = Audit(capture.Factory(), required: false, new LogRecorder());

        await audit.CompleteAsync(SqlExecutionEntry.NotRecorded, new SqlExecutionEnd(QueryExecutionLogStatus.Succeeded, 1, 1));

        capture.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task AReadRunThroughTheLog_IsClosedWithAGenericMessage_NeverTheDatabasesText()
    {
        var audit = new RecordingExecutionAudit();

        var result = await audit.RunAsync(
            new SqlExecutionStart(Source.Id, "SELECT status FROM orders", SqlExecutionContexts.DocumentationEnumDiscovery),
            _ => Task.FromResult(new ProviderQueryResult { Success = false, ErrorMessage = "invalid input syntax: \"Ivan Horvat\"" }),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        audit.Ends.Should().ContainSingle()
            .Which.Should().Match<SqlExecutionEnd>(x => x.Status == QueryExecutionLogStatus.Failed && !x.ErrorMessage!.Contains("Ivan"));
    }

    [Test]
    public void TheCoreServices_RegisterTheExecutionLog()
    {
        var services = new ServiceCollection();
        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Beacon:EncryptionKey"] = TestEncryptionKey.Value })
            .Build();

        services.AddBeaconServices(configuration, x => x.AddBeaconScheduler<NoOpScheduler>());

        services.Should().Contain(x => x.ServiceType == typeof(ISqlExecutionAudit) && x.ImplementationType == typeof(SqlExecutionAudit));
    }

    private static GatedSqlRequest Request(string sql, string? context = null) =>
        new(Source, sql, new Dictionary<string, object?>(), 10, TestRowSecurity.NotApplicable, ExecutionContext: context);

    private static GatedSqlRunner Runner(IDataSourceProvider provider, ISqlExecutionAudit audit) =>
        new(TestSqlGate.Create(), TestQueryServices.Providers(provider), TestConfiguredDatabases.None, TestQueryServices.QueryOptions, executionAudit: audit);

    private static SqlExecutionAudit Audit(IDbContextFactory<BeaconContext> factory, bool required, LogRecorder logs, BeaconActor? actor = null)
    {
        var actors = new Mock<IBeaconActorAccessor>();
        actors
            .Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(actor ?? BeaconActor.Nobody);

        return new SqlExecutionAudit(
            factory,
            actors.Object,
            Options.Create(new SecurityAuditOptions { ExecutionLog = new ExecutionLogOptions { Required = required } }),
            logs.For<SqlExecutionAudit>());
    }

    private static IDbContextFactory<BeaconContext> Unavailable()
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the log store is unavailable"));

        return factory.Object;
    }

    private static Mock<IDataSourceProvider> Answering(ProviderQueryResult result, List<string>? order = null)
    {
        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .Callback(() => order?.Add("execute"))
            .ReturnsAsync(result);

        return provider;
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
