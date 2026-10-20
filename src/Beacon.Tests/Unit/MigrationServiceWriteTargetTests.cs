using System.Text.Json;
using Beacon.Connector.MySql;
using Beacon.Connector.PostgreSql;
using Beacon.Connector.SqlServer;
using Beacon.Core;
using Beacon.Core.Adapters;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Entities.DataMigration;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.DataMigration;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Core.SecurityAudit;

namespace Beacon.Tests.Unit;

/// <summary>
/// The migration service writes only into a migration write target, and only while the deployment runs migrations: a run
/// into a data source no Admin marked, into a marked one that is read-only, or from a write target reads nothing, opens no
/// connection to the destination and is recorded as a failed run with the reason; with migrations off nothing is recorded
/// either. A run into a write target reads its source within the migration's own row budget and writes through the
/// destination's connection. Recording ADO.NET doubles, no database (§4.7).
/// </summary>
[TestFixture]
[NonParallelizable] // DbConnectionFactory is a process-wide registry.
public class MigrationServiceWriteTargetTests
{
    private const int JobId = 1;
    private const int ReadSource = 10;
    private const int WriteTarget = 20;
    private const int OrdinarySource = 30;

    private ProjectAccessWorld _world = null!;
    private Mock<IQueryExecutionPreviewService> _preview = null!;
    private RecordingDbConnection _destination = null!;
    private int _connectionsOpened;

    [SetUp]
    public void SetUp()
    {
        _world = new ProjectAccessWorld();
        _world.Source(ReadSource);
        _world.Source(WriteTarget).AllowMigrationWrites = true;
        _world.Source(OrdinarySource);
        _world.Serve(new List<MigrationExecutionHistory>());

        _preview = new Mock<IQueryExecutionPreviewService>();
        _preview
            .Setup(x => x.ExecuteTemporaryQueryPreview(It.IsAny<QueryData>(), It.IsAny<List<ParameterValue>?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryExecutionResult { Success = true, FinalResult = NoRows() });

        // The destination's table exists; every statement the writer sends is recorded.
        _destination = new RecordingDbConnection { Scalar = _ => true };
        _connectionsOpened = 0;
        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, _ =>
        {
            _connectionsOpened++;
            return _destination;
        });
    }

    [TearDown]
    public void RestoreConnectionFactories()
    {
        new BeaconBuilder(new ServiceCollection(), new ConfigurationBuilder().Build())
            .AddPostgreSqlConnector()
            .AddSqlServerConnector()
            .AddMySqlConnector();
    }

    [Test]
    public async Task ARunIntoAWriteTarget_WritesThroughItsConnection()
    {
        Job(ReadSource, WriteTarget);

        var result = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Completed, result.ErrorMessage);
        _connectionsOpened.Should().Be(1);
        _destination.ExecutedCommands.Should().Contain(x => x.StartsWith("TRUNCATE TABLE", StringComparison.Ordinal));
        _destination.Commits.Should().Be(1);
    }

    // The writer's own connection checks the destination again, on its own: whatever reaches it, a data source that is not a
    // write target (or is marked read-only, or host-managed) gets no connection, and its credentials are never decrypted.
    [TestCase(OrdinarySource, false, false, "*is not a migration write target*")]
    [TestCase(WriteTarget, true, false, "*marked read-only*")]
    [TestCase(WriteTarget, false, true, "*cannot be used by migration jobs*")]
    public void TheWritersConnection_IsRefusedForAnythingButAWriteTarget_BeforeItsCredentialsAreDecrypted(
        int dataSourceId, bool isReadOnly, bool hostManaged, string reason)
    {
        var dataSource = _world.DataSources.First(x => x.Id == dataSourceId);
        dataSource.IsReadOnly = isReadOnly;
        dataSource.HostManagedKey = hostManaged ? "host" : null;
        var encryption = new Mock<IEncryptionService>();

        var act = () => Service(encryption: encryption).CreateDatabaseConnection(dataSource);

        act.Should().Throw<InvalidOperationException>().WithMessage(reason);
        encryption.Verify(x => x.Decrypt(It.IsAny<string>()), Times.Never);
        _connectionsOpened.Should().Be(0);
    }

    [Test]
    public async Task TheWritersConnection_PassesTheTransportCheck_BeforeItOpens()
    {
        Job(ReadSource, WriteTarget);
        var service = Service(transport: TestTransport.Policy(enforceOnExisting: true));

        var connect = () => service.CreateDatabaseConnection(_world.DataSources.First(x => x.Id == WriteTarget));
        var run = await service.ExecuteMigrationJob(Run(), CancellationToken.None);

        connect.Should().Throw<InvalidOperationException>().WithInnerException<DataSourceTransportException>();
        run.Status.Should().Be(MigrationStatus.Failed);
        run.ErrorMessage.Should().Contain("authenticates the server over TLS");
        _connectionsOpened.Should().Be(0, "the destination's connection does not authenticate the server, so it is never opened");
        _destination.ExecutedCommands.Should().BeEmpty();
    }

    [Test]
    public void TheWritersConnection_ToAWriteTarget_UsesItsCredentials()
    {
        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(x => x.Decrypt(It.IsAny<string>())).Returns("Host=unused;Database=unused");

        using var connection = Service(encryption: encryption).CreateDatabaseConnection(_world.DataSources.First(x => x.Id == WriteTarget));

        connection.Should().BeSameAs(_destination);
        encryption.Verify(x => x.Decrypt(It.IsAny<string>()), Times.Once);
    }

    [Test]
    public async Task ARun_IsRecordedAsStarted_BeforeItTouchesADataSource_AndAsEnded_WithItsCounts()
    {
        Job(ReadSource, WriteTarget);
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(3, "ext-ada"));

        var result = await Service(audit: audit).ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Completed, result.ErrorMessage);
        audit.Events.Select(x => x.Action).Should().Equal(SecurityAuditActions.MigrationJobStarted, SecurityAuditActions.MigrationJobRunEnded);
        audit.Ways.Should().Equal("saved", "staged");
        var started = audit.Of(SecurityAuditActions.MigrationJobStarted).Single();
        started.ActorUserId.Should().Be(3);
        started.DetailsJson.Should().Contain($"\"sourceDataSourceId\":{ReadSource}").And.Contain($"\"destinationDataSourceId\":{WriteTarget}");
        var ended = audit.Of(SecurityAuditActions.MigrationJobRunEnded).Single();
        ended.Outcome.Should().Be(SecurityAuditOutcomes.Success);
        ended.DetailsJson.Should().Contain("\"status\":\"Completed\"");
        foreach (var auditEvent in audit.Events)
        {
            SecurityAuditTestKit.AssertNoPii(auditEvent, "Host=unused");
        }
    }

    [Test]
    public async Task ARefusedRun_IsRecordedAsRefused_WithItsReasonCode_NeverTheReasonsText()
    {
        Job(ReadSource, OrdinarySource);
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(3, "ext-ada"));

        await Service(audit: audit).ExecuteMigrationJob(Run(), CancellationToken.None);

        var refused = audit.Single("migration write target");
        refused.Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.MigrationJobRunEnded, Outcome = SecurityAuditOutcomes.Refused },
            x => x.ExcludingMissingMembers());
        refused.DetailsJson.Should().Contain("\"reason\":\"write_target_refused\"");
        audit.Of(SecurityAuditActions.MigrationJobStarted).Should().BeEmpty("a refused run never starts");
    }

    [Test]
    public async Task ARunIntoADataSourceNoAdminMarked_ReadsNothing_AndConnectsToNothing()
    {
        Job(ReadSource, OrdinarySource);

        var result = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Failed);
        result.ErrorMessage.Should().Contain("is not a migration write target");
        RefusedAndRecorded(result.ErrorMessage!);
    }

    [Test]
    public async Task ARunIntoAWriteTargetMarkedReadOnly_ReadsNothing_AndConnectsToNothing()
    {
        Job(ReadSource, WriteTarget);
        _world.DataSources.First(x => x.Id == WriteTarget).IsReadOnly = true;

        var result = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Failed);
        result.ErrorMessage.Should().Contain("marked read-only");
        RefusedAndRecorded(result.ErrorMessage!);
    }

    [Test]
    public async Task ARunReadingAWriteTarget_ThroughAStep_ReadsNothing_AndConnectsToNothing()
    {
        Job(ReadSource, WriteTarget, StepsOn(WriteTarget));

        var result = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Failed);
        result.ErrorMessage.Should().Be(MigrationWriteTargets.ReadRefusedMessage);
        RefusedAndRecorded(MigrationWriteTargets.ReadRefusedMessage);
    }

    [Test]
    public async Task WhileTheDeploymentDoesNotRunMigrations_ARunReadsNothing_AndConnectsToNothing()
    {
        Job(ReadSource, WriteTarget);

        var result = await Service(enabled: false).ExecuteMigrationJob(Run(), CancellationToken.None);
        var created = await Service(enabled: false).CreateMigrationJob(new CreateMigrationJobRequest(), CancellationToken.None);

        result.ErrorMessage.Should().Be(MigrationWriteTargets.DisabledMessage);
        created.Success.Should().BeFalse();
        created.ErrorMessage.Should().Be(MigrationWriteTargets.DisabledMessage);
        NothingRan();
    }

    [Test]
    public async Task ARun_ReadsItsSourceWithinTheMigrationRowBudget_NotTheGeneralCap()
    {
        Job(ReadSource, WriteTarget);

        await Service(maxRows: 250_000).ExecuteMigrationJob(Run(), CancellationToken.None);

        _preview.Verify(
            x => x.ExecuteTemporaryQueryPreview(It.IsAny<QueryData>(), It.IsAny<List<ParameterValue>?>(), 250_000, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // The job form sends plain SQL with the source in DataSourceId: the run reads it from that source, the data source its
    // access and write policy were decided on.
    [Test]
    public async Task APlainSqlJob_RunsItsSqlOnTheJobsSource()
    {
        Job(ReadSource, WriteTarget, "SELECT id FROM accounts");
        QueryData? read = null;
        _preview
            .Setup(x => x.ExecuteTemporaryQueryPreview(It.IsAny<QueryData>(), It.IsAny<List<ParameterValue>?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<QueryData, List<ParameterValue>?, int, CancellationToken>((x, _, _, _) => read = x)
            .ReturnsAsync(new QueryExecutionResult { Success = true, FinalResult = NoRows() });

        var result = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Completed, result.ErrorMessage);
        read!.Steps.Should().ContainSingle().Which.Should().Match<QueryStepData>(
            x => x.DataSourceId == ReadSource && x.SqlValue == "SELECT id FROM accounts");
    }

    [Test]
    public async Task AJsonStepsJob_RunsEachStepOnItsOwnDataSource()
    {
        Job(ReadSource, WriteTarget, StepsOn(ReadSource, OrdinarySource));
        QueryData? read = null;
        _preview
            .Setup(x => x.ExecuteTemporaryQueryPreview(It.IsAny<QueryData>(), It.IsAny<List<ParameterValue>?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<QueryData, List<ParameterValue>?, int, CancellationToken>((x, _, _, _) => read = x)
            .ReturnsAsync(new QueryExecutionResult { Success = true, FinalResult = NoRows() });

        var result = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Completed, result.ErrorMessage);
        read!.Steps.Select(x => x.DataSourceId).Should().Equal(ReadSource, OrdinarySource);
    }

    // A plain-SQL job whose source is a write target is refused like a step that reads one: the run reads nothing.
    [Test]
    public async Task APlainSqlJobReadingAWriteTarget_ReadsNothing_AndConnectsToNothing()
    {
        Job(WriteTarget, WriteTarget, "SELECT id FROM accounts");

        var result = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Failed);
        result.ErrorMessage.Should().Be(MigrationWriteTargets.ReadRefusedMessage);
        RefusedAndRecorded(MigrationWriteTargets.ReadRefusedMessage);
    }

    // A source read that ran out of time has no rows. Written in Truncate mode they would empty the destination and the
    // run would read as completed; instead the run fails before the destination is opened, and its history says so.
    [Test]
    public async Task ASourceReadThatTimedOut_WritesNothing_AndTheRunIsRecordedFailed()
    {
        Job(ReadSource, WriteTarget);
        var timedOut = NoRows();
        timedOut.TimedOut = true;
        _preview
            .Setup(x => x.ExecuteTemporaryQueryPreview(It.IsAny<QueryData>(), It.IsAny<List<ParameterValue>?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryExecutionResult { Success = true, FinalResult = timedOut });

        var result = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Failed);
        result.ErrorMessage.Should().Contain("timed out");
        NothingWritten();
    }

    // The final query of the source read fails the run when it times out (QueryService throws), and so does a failed step.
    [Test]
    public async Task ASourceReadThatFailed_InTheFinalQueryOrAStep_WritesNothing_AndTheRunIsRecordedFailed()
    {
        Job(ReadSource, WriteTarget);
        _preview
            .Setup(x => x.ExecuteTemporaryQueryPreview(It.IsAny<QueryData>(), It.IsAny<List<ParameterValue>?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BeaconException("The final query timed out."));

        var thrown = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        thrown.Status.Should().Be(MigrationStatus.Failed);
        thrown.ErrorMessage.Should().Contain("The final query timed out.");
        NothingWritten();

        _world.Saved.Clear();
        _preview
            .Setup(x => x.ExecuteTemporaryQueryPreview(It.IsAny<QueryData>(), It.IsAny<List<ParameterValue>?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryExecutionResult
            {
                Success = true,
                FinalResult = NoRows(),
                StepResults = [new QueryStepResult { StepOrder = 1, Success = false, ErrorMessage = "Step execution timed out" }]
            });

        var failedStep = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        failedStep.Status.Should().Be(MigrationStatus.Failed);
        NothingWritten();
    }

    private void NothingWritten()
    {
        _connectionsOpened.Should().Be(0, "nothing is written from a failed source read");
        _destination.ExecutedCommands.Should().BeEmpty();
        _world.Saved.OfType<MigrationExecutionHistory>().Should().ContainSingle().Which.Status.Should().Be(MigrationStatus.Failed);
    }

    private void NothingRan()
    {
        NothingReadOrWritten();
        _world.Saved.OfType<MigrationExecutionHistory>().Should().BeEmpty("a run the deployment lock refuses is not recorded");
    }

    // A refused run reads and writes nothing, and its history says why it did not copy anything.
    private void RefusedAndRecorded(string reason)
    {
        NothingReadOrWritten();
        _world.Saved.OfType<MigrationExecutionHistory>().Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            MigrationJobId = JobId,
            Status = MigrationStatus.Failed,
            ErrorMessage = reason
        });
    }

    private void NothingReadOrWritten()
    {
        _preview.Verify(
            x => x.ExecuteTemporaryQueryPreview(It.IsAny<QueryData>(), It.IsAny<List<ParameterValue>?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _connectionsOpened.Should().Be(0, "no connection is made with a destination's credentials");
        _destination.ExecutedCommands.Should().BeEmpty();
    }

    private MigrationService Service(
        bool enabled = true,
        int maxRows = MigrationOptions.DefaultMaxRows,
        Mock<IEncryptionService>? encryption = null,
        ISecurityAuditRecorder? audit = null,
        IDataSourceTransportPolicy? transport = null)
    {
        if (encryption == null)
        {
            encryption = new Mock<IEncryptionService>();
            encryption.Setup(x => x.Decrypt(It.IsAny<string>())).Returns("Host=unused;Database=unused");
        }

        return new MigrationService(
            _world.Factory(),
            TestTransport.Resolver(encryption.Object, transport),
            Mock.Of<IQueryService>(),
            _preview.Object,
            Mock.Of<IBeaconUserContext>(),
            new MigrationBulkContextFactory([]),
            MigrationTestWriters.All(),
            NullLogger<MigrationService>.Instance,
            Options.Create(new MigrationOptions { Enabled = enabled, MaxRows = maxRows }),
            TestChangeApprovals.Submitter(), TestProjectAccess.Actors(), audit ?? new RecordingSecurityAudit(), TimeProvider.System);
    }

    private void Job(int source, int destination, string? queryText = null)
    {
        _world.Serve(new List<MigrationJob>
        {
            new()
            {
                Id = JobId,
                Name = "copy",
                Description = "copy rows",
                DataSourceId = source,
                DataSource = _world.DataSources.First(x => x.Id == source),
                DestinationDataSourceId = destination,
                DestinationDataSource = _world.DataSources.First(x => x.Id == destination),
                QueryText = queryText ?? StepsOn(source),
                DestinationTable = "target",
                Mode = MigrationMode.Truncate,
                IsEnabled = true
            }
        });
    }

    private static ExecuteMigrationJobRequest Run() => new(JobId, IsManualExecution: true, ExecutionContext: "test");

    private static string StepsOn(params int[] dataSourceIds)
    {
        return JsonSerializer.Serialize(dataSourceIds
            .Select((x, i) =>
                new
                {
                    StepOrder = i + 1,
                    Name = $"step {i + 1}",
                    SqlValue = "SELECT id FROM accounts",
                    DataSourceId = x
                }));
    }

    private static QueryResult NoRows()
    {
        return new QueryResult
        {
            QueryResults = "",
            TotalRecords = 0,
            DataSourceName = "source",
            SqlQuery = "SELECT id FROM accounts",
            SubscriptionName = "",
            SubscriptionId = null
        };
    }
}
