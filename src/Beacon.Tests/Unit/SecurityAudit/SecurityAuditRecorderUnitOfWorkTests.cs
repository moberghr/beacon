using System.Data.Common;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.SecurityAudit;
using Beacon.Tests.Common;
using Recorder = Beacon.Core.SecurityAudit.SecurityAuditRecorder;

namespace Beacon.Tests.Unit.SecurityAudit;

/// <summary>
/// The recorder's units of work: a change and the events built from it commit together or not at all, on the caller's
/// transaction or on one the recorder opens through the execution strategy; an attempt the strategy retries saves no event
/// twice and never commits a change without its event; a settled caller is resolved before the change takes its locks, and
/// an entry that names its actor never resolves the caller of a request not authenticated yet; and an event written on its
/// own that cannot be written reaches the log stream with its fields, within a bounded time.
/// </summary>
[TestFixture]
public class SecurityAuditRecorderUnitOfWorkTests
{
    [Test]
    public async Task AFailedEventSave_RollsTheChangeBack_AndTheExceptionReachesTheCaller()
    {
        // The delete is answered; the event's INSERT is not, so it fails as a database would.
        var capture = new SqlCapture().ThenRowsAffected(1);
        var factory = capture.Factory();
        await using var context = await factory.CreateDbContextAsync();

        var act = () => RecorderOver(factory).SaveWithEventsAsync(
            context,
            () => context.ProjectMembers
                .Where(x => x.ProjectId == 4)
                .ExecuteDeleteAsync(),
            x => [new SecurityAuditEntry(SecurityAuditActions.MemberRemoved).On(SecurityAuditResources.Project, 4)],
            CancellationToken.None);

        (await act.Should().ThrowAsync<DbUpdateException>()).WithInnerException<SqlCapturedException>();
        capture.TransactionIsolationLevels.Should().ContainSingle("the recorder opened one transaction");
        capture.Commits.Should().Be(0, "neither the change nor the event commits");
        capture.Commands.Should().HaveCount(2);
        capture.Commands[1].Should().Contain("INSERT INTO beacon.security_audit_events");
    }

    [Test]
    public async Task InTheCallersTransaction_AFailedEventSave_ReachesTheCaller_AndNothingIsCommitted()
    {
        var capture = new SqlCapture().ThenRowsAffected(1);
        var factory = capture.Factory();
        await using var context = await factory.CreateDbContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();

        var act = () => RecorderOver(factory).SaveWithEventsAsync(
            context,
            () => context.ProjectMembers
                .Where(x => x.ProjectId == 4)
                .ExecuteDeleteAsync(),
            x => [new SecurityAuditEntry(SecurityAuditActions.MemberRemoved).On(SecurityAuditResources.Project, 4)],
            CancellationToken.None);

        (await act.Should().ThrowAsync<DbUpdateException>()).WithInnerException<SqlCapturedException>();
        capture.TransactionIsolationLevels.Should().ContainSingle("the caller's transaction is the only one: none is opened, none committed");
        capture.Commits.Should().Be(0);
    }

    [Test]
    public async Task ARetriedAttempt_RunsTheChangeAgainOnACleanTracker_AndSavesItsEventOnce()
    {
        await using var context = new RetryingContext(failingSave: 1);
        var runs = 0;

        await RecorderOver(Mock.Of<IDbContextFactory<BeaconContext>>()).SaveWithEventsAsync(
            context,
            () => Task.FromResult(++runs),
            x => [new SecurityAuditEntry(SecurityAuditActions.TaskClaimed).On(SecurityAuditResources.Task, 5).With(new SecurityAuditDetails().Add("run", x))],
            CancellationToken.None);

        runs.Should().Be(2, "the strategy retried the attempt whose event save failed");
        var committed = context.CommittedEvents();
        committed.Should().ContainSingle("the failed attempt's event is not saved again with the retried one")
            .Which.DetailsJson.Should().Be("{\"run\":2}");
        context.Commits.Should().Be(1);
    }

    [Test]
    public async Task ARetriedAttempt_OfTrackedChangesNeverSaved_SavesThemAndTheirEventOnce()
    {
        // The first attempt fails in the change's own save: nothing was accepted, so the retry saves the change again.
        await using var context = new RetryingContext(failingSave: 1);
        var project = new Project { Name = "Finance" };
        context.Projects.Add(project);

        await RecorderOver(Mock.Of<IDbContextFactory<BeaconContext>>()).SaveWithEventsAsync(
            context,
            () => [new SecurityAuditEntry(SecurityAuditActions.ProjectCreated).On(SecurityAuditResources.Project, project.Id)],
            CancellationToken.None);

        context.Commits.Should().Be(1);
        context.CommittedEntities().OfType<Project>().Should().ContainSingle();
        context.CommittedEvents().Should().ContainSingle().Which.ResourceId.Should().Be(project.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task ARetriedAttempt_OfTrackedChangesAnEarlierSaveAccepted_FailsWithNothingCommitted()
    {
        // The change's save succeeds and is accepted; the event's save fails: a retry could only lose the change or save
        // the event twice, so it fails instead.
        await using var context = new RetryingContext(failingSave: 2);
        context.Projects.Add(new Project { Name = "Finance" });

        var act = () => RecorderOver(Mock.Of<IDbContextFactory<BeaconContext>>()).SaveWithEventsAsync(
            context,
            () => [new SecurityAuditEntry(SecurityAuditActions.ProjectCreated).On(SecurityAuditResources.Project, 1)],
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(Recorder.NotReplayableMessage);
        context.Commits.Should().Be(0);
        context.ChangeTracker.Entries<SecurityAuditEvent>().Should().BeEmpty("the failed attempt's event is not left to a later save");
    }

    [TestCase(false, TestName = "TheCaller_IsResolvedBeforeTheChangeRuns_InWorkWithoutARequest")]
    [TestCase(true, TestName = "TheCaller_IsResolvedBeforeTheChangeRuns_InAnAuthenticatedRequest")]
    public async Task TheCaller_IsResolvedBeforeTheChangeRuns(bool authenticatedRequest)
    {
        var timeline = new List<string>();
        var actors = new Mock<IBeaconActorAccessor>();
        actors
            .Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .Callback(() => timeline.Add("caller"))
            .ReturnsAsync(RecordingSecurityAudit.User(3, "ext-ada"));
        await using var context = new RetryingContext(failingSave: 0);
        var recorder = new Recorder(
            Mock.Of<IDbContextFactory<BeaconContext>>(),
            actors.Object,
            SecurityAuditTestKit.Integrity,
            SecurityAuditTestKit.CallerHasher,
            TimeProvider.System,
            LoggerFactory.Create(_ => { }),
            authenticatedRequest ? Request(authenticated: true) : new HttpContextAccessor());

        await recorder.SaveWithEventsAsync(
            context,
            () =>
            {
                timeline.Add("change");

                return Task.FromResult(1);
            },
            _ => [new SecurityAuditEntry(SecurityAuditActions.TaskClaimed).On(SecurityAuditResources.Task, 5)],
            CancellationToken.None);

        timeline.Should().Equal(["caller", "change"], "nothing is read on another connection while the change holds its locks");
        context.CommittedEvents().Should().ContainSingle().Which.ActorUserId.Should().Be(3);
    }

    // A request that signs in, runs first-run setup or provisions its caller on its first call is not authenticated yet:
    // an entry that names its actor never resolves the request's caller, which would fix the scope's caller as anonymous
    // before authentication sets it (and the provisioned caller would then be refused its projects).
    [Test]
    public async Task AnEntryThatNamesItsActor_NeverResolvesTheRequestsCaller_WhileTheRequestIsNotAuthenticated()
    {
        var actors = new Mock<IBeaconActorAccessor>(MockBehavior.Strict);
        await using var context = new RetryingContext(failingSave: 0);
        var own = new RetryingContext(failingSave: 0);
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(own);
        var logs = new LogRecorder();
        var recorder = new Recorder(
            factory.Object,
            actors.Object,
            SecurityAuditTestKit.Integrity,
            SecurityAuditTestKit.CallerHasher,
            TimeProvider.System,
            LoggerFactory.Create(x => x.AddProvider(logs)),
            Request(authenticated: false));

        await recorder.StageAsync(
            context,
            new SecurityAuditEntry(SecurityAuditActions.PasswordChanged).On(SecurityAuditResources.User, 7).By(SecurityAuditActor.OfUser(7)),
            CancellationToken.None);
        await recorder.SaveWithEventsAsync(
            context,
            () => [new SecurityAuditEntry(SecurityAuditActions.UserProvisioned).On(SecurityAuditResources.User, 17).By(SecurityAuditActor.System)],
            CancellationToken.None);
        await recorder.SaveWithEventsAsync(
            context,
            () => Task.FromResult(1),
            _ => [new SecurityAuditEntry(SecurityAuditActions.SuperAdminCreated).On(SecurityAuditResources.User, 1).By(SecurityAuditActor.Anonymous)],
            CancellationToken.None);
        await recorder.RecordAsync(
            new SecurityAuditEntry(SecurityAuditActions.SetupTokenRefused).Ending(SecurityAuditOutcomes.Refused).By(SecurityAuditActor.Anonymous),
            CancellationToken.None);

        actors.Verify(x => x.GetCurrentAsync(It.IsAny<CancellationToken>()), Times.Never);
        context.CommittedEvents().Select(x => x.ActorKind).Should().Equal(
            SecurityAuditActorKinds.User,
            SecurityAuditActorKinds.System,
            SecurityAuditActorKinds.Anonymous);
        logs.Entries.Should().NotContain(x => x.EventId.Id == SecurityAuditLogEvents.WriteFailed.Id, "the event written on its own was stored");
    }

    // An entry without an actor in a request that is not authenticated yet is by that request's caller, resolved only for
    // it: anonymous, which the accessor answers without reading anything while the change holds its locks.
    [Test]
    public async Task WhileTheRequestIsNotAuthenticated_TheCallerIsResolvedOnlyForAnEntryWithoutAnActor()
    {
        var timeline = new List<string>();
        var actors = new Mock<IBeaconActorAccessor>();
        actors
            .Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .Callback(() => timeline.Add("caller"))
            .ReturnsAsync(BeaconActor.Nobody);
        await using var context = new RetryingContext(failingSave: 0);
        var recorder = new Recorder(
            Mock.Of<IDbContextFactory<BeaconContext>>(),
            actors.Object,
            SecurityAuditTestKit.Integrity,
            SecurityAuditTestKit.CallerHasher,
            TimeProvider.System,
            LoggerFactory.Create(_ => { }),
            Request(authenticated: false));

        await recorder.SaveWithEventsAsync(
            context,
            () =>
            {
                timeline.Add("change");

                return Task.FromResult(1);
            },
            _ =>
            [
                new SecurityAuditEntry(SecurityAuditActions.UserProvisioned).On(SecurityAuditResources.User, 17).By(SecurityAuditActor.System),
                new SecurityAuditEntry(SecurityAuditActions.SignedOut).On(SecurityAuditResources.User, 17),
                new SecurityAuditEntry(SecurityAuditActions.SignedOut).On(SecurityAuditResources.User, 18)
            ],
            CancellationToken.None);

        timeline.Should().Equal(["change", "caller"], "resolved once, and only for the entries that name no actor");
        context.CommittedEvents().Select(x => x.ActorKind).Should().Equal(
            SecurityAuditActorKinds.System,
            SecurityAuditActorKinds.Anonymous,
            SecurityAuditActorKinds.Anonymous);
    }

    [Test]
    public async Task AWriteOnItsOwn_ThatFails_GoesToTheLogStream_WithTheEventsFields_NotPersisted()
    {
        var logs = new LogRecorder();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the database is down"));

        await RecorderOver(factory.Object, logs).RecordAsync(
            new SecurityAuditEntry(SecurityAuditActions.PasswordSignInFailed)
                .On(SecurityAuditResources.User, 7)
                .Ending(SecurityAuditOutcomes.Failure)
                .By(SecurityAuditActor.Anonymous)
                .With(new SecurityAuditDetails().AddCode("reason", "invalid_credentials")),
            CancellationToken.None);

        var lost = logs.Entries.Should().ContainSingle(x => x.EventId.Id == SecurityAuditLogEvents.WriteFailed.Id).Subject;
        lost.Level.Should().Be(LogLevel.Error);
        lost.Message.Should()
            .Contain("Action=auth.password_sign_in_failed")
            .And.Contain("Outcome=failure")
            .And.Contain("Persisted=False")
            .And.Contain("ActorKind=anonymous")
            .And.Contain("ResourceType=user")
            .And.Contain("ResourceId=7")
            .And.Contain("{\"reason\":\"invalid_credentials\"}")
            .And.NotContain("the database is down");
    }

    [Test]
    public async Task AWriteOnItsOwn_WhoseCallerCannotBeResolved_StillGoesToTheLogStream_WithWhatTheEntrySays()
    {
        var logs = new LogRecorder();
        var actors = new Mock<IBeaconActorAccessor>();
        actors
            .Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the user store is down"));
        var recorder = new Recorder(
            Mock.Of<IDbContextFactory<BeaconContext>>(),
            actors.Object,
            SecurityAuditTestKit.Integrity,
            SecurityAuditTestKit.CallerHasher,
            TimeProvider.System,
            LoggerFactory.Create(x => x.AddProvider(logs)),
            new HttpContextAccessor());

        await recorder.RecordAsync(new SecurityAuditEntry(SecurityAuditActions.SignedOut).On(SecurityAuditResources.User, 7), CancellationToken.None);

        logs.Entries.Should().ContainSingle(x => x.EventId.Id == SecurityAuditLogEvents.WriteFailed.Id)
            .Which.Message.Should().Contain("Action=auth.signed_out").And.Contain($"ActorKind={Recorder.UnresolvedActorKind}").And.Contain("Persisted=False");
    }

    [Test]
    public async Task AWriteOnItsOwn_ToADatabaseThatDoesNotAnswer_IsGivenUpAfterItsTimeout()
    {
        var logs = new LogRecorder();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken token) =>
            {
                await Task.Delay(Timeout.Infinite, token);

                return (BeaconContext)NpgsqlTestContext.Create();
            });
        var recorder = new Recorder(
            factory.Object,
            TestProjectAccess.Actors(),
            SecurityAuditTestKit.Integrity,
            SecurityAuditTestKit.CallerHasher,
            time,
            LoggerFactory.Create(x => x.AddProvider(logs)),
            new HttpContextAccessor());

        var write = recorder.RecordAsync(new SecurityAuditEntry(SecurityAuditActions.SetupTokenRefused).By(SecurityAuditActor.Anonymous), CancellationToken.None);
        write.IsCompleted.Should().BeFalse();
        time.Advance(Recorder.StandaloneWriteTimeout);

        (await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(write, "the write gives up when its timeout passes");
        await write;
        logs.Entries.Should().ContainSingle(x => x.EventId.Id == SecurityAuditLogEvents.WriteFailed.Id)
            .Which.Message.Should().Contain("Action=auth.setup_token_refused").And.Contain("Persisted=False");
    }

    [Test]
    public async Task AStagedEvent_TheContextNeverSaves_FailsTheSharedAssertion()
    {
        var audit = new RecordingSecurityAudit();
        await using var saved = new RecordingBeaconContext(new Dictionary<Type, object>(), []);
        await using var neverSaved = new RecordingBeaconContext(new Dictionary<Type, object>(), []);

        await audit.StageAsync(saved, new SecurityAuditEntry(SecurityAuditActions.UserDisabled).On(SecurityAuditResources.User, 7), CancellationToken.None);
        await saved.SaveChangesAsync();
        audit.AssertStagedEventsSaved();
        await saved.SaveChangesAsync();
        await audit.StageAsync(saved, new SecurityAuditEntry(SecurityAuditActions.UserEnabled).On(SecurityAuditResources.User, 7), CancellationToken.None);
        await audit.StageAsync(neverSaved, new SecurityAuditEntry(SecurityAuditActions.UserArchived).On(SecurityAuditResources.User, 7), CancellationToken.None);

        var act = audit.AssertStagedEventsSaved;

        act.Should().Throw<Exception>().WithMessage("*identity.user_enabled*identity.user_archived*");
    }

    // A request in progress: signed in (a principal authentication set), or not authenticated yet.
    private static IHttpContextAccessor Request(bool authenticated)
    {
        var httpContext = new DefaultHttpContext();
        if (authenticated)
        {
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ext-ada")], "Test"));
        }

        return new HttpContextAccessor { HttpContext = httpContext };
    }

    private static Recorder RecorderOver(IDbContextFactory<BeaconContext> factory, LogRecorder? logs = null)
    {
        return new Recorder(
            factory,
            TestProjectAccess.Actors(3, "ext-ada"),
            SecurityAuditTestKit.Integrity,
            SecurityAuditTestKit.CallerHasher,
            TimeProvider.System,
            LoggerFactory.Create(x => x.AddProvider(logs ?? new LogRecorder())),
            new HttpContextAccessor());
    }

    /// <summary>A failure the retrying strategy below treats as transient, as a provider's strategy treats a dropped connection.</summary>
    private sealed class TransientTestException() : Exception("transient");

    /// <summary>Retries an attempt that threw <see cref="TransientTestException"/>, at once, up to three times.</summary>
    private sealed class RetryOnTransient(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is TransientTestException;

        protected override TimeSpan? GetNextDelay(Exception lastException) => base.GetNextDelay(lastException) == null ? null : TimeSpan.Zero;
    }

    /// <summary>
    /// A context on a retrying execution strategy that never reaches a database: transactions are stand-ins, and each save
    /// records what it writes (generated ids assigned, changes accepted as EF accepts them) under the transaction it ran in,
    /// except save number <c>failingSave</c>, which fails transiently and accepts nothing.
    /// </summary>
    private sealed class RetryingContext : BeaconContext
    {
        private readonly int _failingSave;
        private readonly CommitLog _commits;
        private readonly List<(Guid? Transaction, object Entity)> _saved = [];
        private int _saves;
        private int _nextId = 41;

        public RetryingContext(int failingSave)
            : this(failingSave, new CommitLog())
        {
        }

        private RetryingContext(int failingSave, CommitLog commits)
            : base(OptionsFor(commits), "beacon")
        {
            _failingSave = failingSave;
            _commits = commits;
        }

        public int Commits => _commits.Committed.Count;

        public List<object> CommittedEntities()
        {
            return _saved
                .Where(x => x.Transaction != null && _commits.Committed.Contains(x.Transaction.Value))
                .Select(x => x.Entity)
                .ToList();
        }

        public List<SecurityAuditEvent> CommittedEvents()
        {
            return CommittedEntities()
                .OfType<SecurityAuditEvent>()
                .ToList();
        }

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            if (++_saves == _failingSave)
            {
                throw new TransientTestException();
            }

            var pending = ChangeTracker.Entries()
                .Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                .ToList();
            var transaction = Database.CurrentTransaction?.TransactionId;
            foreach (var entry in pending)
            {
                if (entry.State == EntityState.Added && entry.Metadata.FindProperty("Id") is { } id && entry.Property(id.Name).IsTemporary)
                {
                    entry.Property(id.Name).CurrentValue = _nextId++;
                }

                _saved.Add((transaction, entry.Entity));
            }

            if (acceptAllChangesOnSuccess)
            {
                ChangeTracker.AcceptAllChanges();
            }

            return Task.FromResult(pending.Count);
        }

        private static DbContextOptions<RetryingContext> OptionsFor(CommitLog commits)
        {
            return new DbContextOptionsBuilder<RetryingContext>()
                .UseNpgsql("Host=localhost;Database=never_opened", x => x.ExecutionStrategy(y => new RetryOnTransient(y)))
                .UseSnakeCaseNamingConvention()
                .AddInterceptors([.. new SqlCapture().OfflineInterceptors(), commits])
                .Options;
        }
    }

    /// <summary>The transactions that committed, by id.</summary>
    private sealed class CommitLog : DbTransactionInterceptor
    {
        public HashSet<Guid> Committed { get; } = [];

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Committed.Add(eventData.TransactionId);

            return Task.CompletedTask;
        }

        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
        {
            Committed.Add(eventData.TransactionId);
        }
    }
}
