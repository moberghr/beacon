using System.Diagnostics.Metrics;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Mcp;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Telemetry;
using Beacon.Tests.Common;
using Recorder = Beacon.Core.SecurityAudit.SecurityAuditRecorder;

namespace Beacon.Tests.Unit.SecurityAudit;

/// <summary>
/// The recorder: the request's caller becomes the event's actor by kind (a user by its <c>Users.Id</c>, an API key with its
/// id, a caller without a Beacon user by a keyed hash, background work as the system), never by a name or an external id;
/// a staged event joins the caller's save; events that need what a save produces are saved with it in one transaction; and
/// an event written on its own never fails what it records — a failed write is logged at Error (9107) and counted, and a
/// write is not cancelled with the request.
/// </summary>
[TestFixture]
public class SecurityAuditRecorderTests
{
    [Test]
    public void TheCaller_BecomesTheActor_ByKind_NeverByAnExternalId()
    {
        var hasher = SecurityAuditTestKit.CallerHasher;
        var mcpPrincipal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(McpCallerClaimTypes.CallerHash, "hash-of-subject")], "mcp"));

        var user = Recorder.ActorOf(new BeaconActor("ext-ana", false) { Kind = BeaconPrincipalKind.User, BeaconUserId = 7 }, null, hasher);
        var key = Recorder.ActorOf(new BeaconActor("ext-ana", false) { Kind = BeaconPrincipalKind.ApiKey, BeaconUserId = 7, ApiKeyId = 12 }, null, hasher);
        var mcpWithoutUser = Recorder.ActorOf(new BeaconActor(null, false) { Kind = BeaconPrincipalKind.McpCaller }, mcpPrincipal, hasher);
        var mcpWithUser = Recorder.ActorOf(new BeaconActor("ext-ana", false) { Kind = BeaconPrincipalKind.McpCaller, BeaconUserId = 7 }, mcpPrincipal, hasher);
        var hostSession = Recorder.ActorOf(new BeaconActor("host-subject-1", true) { Kind = BeaconPrincipalKind.Session }, null, hasher);
        var system = Recorder.ActorOf(BeaconActor.OffRequest, null, hasher);
        var owner = Recorder.ActorOf(new BeaconActor("ext-ana", false) { Kind = BeaconPrincipalKind.Owner, BeaconUserId = 7 }, null, hasher);
        var nobody = Recorder.ActorOf(BeaconActor.Nobody, null, hasher);

        user.Should().Be(new SecurityAuditActor(SecurityAuditActorKinds.User, 7));
        key.Should().Be(new SecurityAuditActor(SecurityAuditActorKinds.ApiKey, 7, 12));
        mcpWithoutUser.Should().Be(new SecurityAuditActor(SecurityAuditActorKinds.McpCaller, CallerHash: "hash-of-subject"));
        mcpWithUser.Should().Be(new SecurityAuditActor(SecurityAuditActorKinds.McpCaller, 7));
        hostSession.Should().Be(new SecurityAuditActor(SecurityAuditActorKinds.Session, CallerHash: hasher.Hash(Recorder.SessionTenant, "host-subject-1")));
        hostSession.CallerHash.Should().NotContain("host-subject-1");
        system.Should().Be(SecurityAuditActor.System);
        owner.Should().Be(new SecurityAuditActor(SecurityAuditActorKinds.System, 7));
        nobody.Should().Be(SecurityAuditActor.Anonymous);
    }

    [Test]
    public async Task AStagedEvent_JoinsTheCallersContext_SealedAndUnsaved_WithTheCallerAsActor()
    {
        using var context = NpgsqlTestContext.Create();
        var recorder = RecorderOver(new SqlCapture().Factory(), new LogRecorder());

        await recorder.StageAsync(
            context,
            new SecurityAuditEntry(SecurityAuditActions.MemberAdded)
                .On(SecurityAuditResources.Project, 4)
                .InProject(4)
                .With(new SecurityAuditDetails().Add("memberUserId", 8).Add("role", ProjectRole.Editor)),
            CancellationToken.None);

        var entry = context.ChangeTracker.Entries<SecurityAuditEvent>().Should().ContainSingle().Subject;
        entry.State.Should().Be(EntityState.Added);
        entry.Entity.Should().BeEquivalentTo(new
        {
            Category = SecurityAuditCategories.Access,
            Action = SecurityAuditActions.MemberAdded,
            Outcome = SecurityAuditOutcomes.Success,
            ActorKind = SecurityAuditActorKinds.User,
            ActorUserId = (int?)3,
            ProjectId = (int?)4,
            ResourceType = SecurityAuditResources.Project,
            ResourceId = "4",
            DetailsJson = "{\"memberUserId\":8,\"role\":\"Editor\"}"
        });
        SecurityAuditTestKit.Integrity.Verify(entry.Entity).Should().Be(SecurityAuditVerdict.Intact);
    }

    // An entry names people by the external id a resource recorded; the event names them by Users.Id, read through the
    // caller's own context (its connection and transaction: no second connection while the change holds its locks;
    // archived users included), and never carries the external id.
    [TestCase(false, "FROM beacon.users", "u.archived_time IS NULL")]
    [TestCase(true, "FROM [beacon].[Users]", "[u].[ArchivedTime] IS NULL")]
    public async Task ThePeopleAnEntryNames_AreWrittenAsUsersIds_ThroughTheCallersContext_Translates(bool sqlServer, string from, string archivedFilter)
    {
        var capture = (sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture()).ThenRows(["ext-ben", 8]);
        var factory = new Mock<IDbContextFactory<BeaconContext>>(MockBehavior.Strict);
        await using var context = await capture.Factory().CreateDbContextAsync();

        await RecorderOver(factory.Object, new LogRecorder()).StageAsync(
            context,
            new SecurityAuditEntry(SecurityAuditActions.OwnerChanged)
                .On(SecurityAuditResources.Subscription, 5)
                .With(new SecurityAuditDetails().Add("ownerUserId", 9))
                .Naming("previousOwnerUserId", "ext-ben")
                .Naming("approverUserId", "ext-unknown"),
            CancellationToken.None);

        var auditEvent = context.ChangeTracker.Entries<SecurityAuditEvent>().Should().ContainSingle().Subject.Entity;
        auditEvent.DetailsJson.Should().Be("{\"ownerUserId\":9,\"previousOwnerUserId\":8,\"approverUserId\":null}");
        SecurityAuditTestKit.AssertNoPii(auditEvent, "ext-ben", "ext-unknown");
        SecurityAuditTestKit.Integrity.Verify(auditEvent).Should().Be(SecurityAuditVerdict.Intact);
        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain(from).And.NotContain(archivedFilter).And.NotContain("ext-ben", "the external ids are a parameter");
    }

    [Test]
    public async Task AnEntryNamingNobody_ReadsNoUser()
    {
        var capture = new SqlCapture();
        using var context = NpgsqlTestContext.Create();

        await RecorderOver(capture.Factory(), new LogRecorder()).StageAsync(
            context,
            new SecurityAuditEntry(SecurityAuditActions.TaskClaimed).On(SecurityAuditResources.Task, 5).Naming("previousAssigneeUserId", null),
            CancellationToken.None);

        capture.Commands.Should().BeEmpty();
        context.ChangeTracker.Entries<SecurityAuditEvent>().Single().Entity.DetailsJson.Should().Be("{\"previousAssigneeUserId\":null}");
    }

    [Test]
    public async Task EventsThatNeedTheSave_AreSavedAfterIt_InOneTransactionTheRecorderOpens()
    {
        var capture = new SqlCapture();
        var saved = new List<object>();
        var project = new Project { Name = "Finance" };
        var saves = new List<int>();

        // The first save generates the project's id, as the database would.
        await using var context = new RecordingBeaconContext(
            new Dictionary<Type, object>(),
            saved,
            capture,
            () =>
            {
                saves.Add(saved.Count);
                project.Id = 41;
            });
        context.Projects.Add(project);

        await RecorderOver(capture.Factory(), new LogRecorder()).SaveWithEventsAsync(
            context,
            () => [new SecurityAuditEntry(SecurityAuditActions.ProjectCreated).On(SecurityAuditResources.Project, project.Id)],
            CancellationToken.None);

        saves.Should().Equal([1, 3], "the change is saved first, then the event, in a second save");
        saved.OfType<SecurityAuditEvent>().Should().ContainSingle().Which.ResourceId.Should().Be("41", "the event names the id the first save generated");
        capture.TransactionIsolationLevels.Should().ContainSingle("both saves run in the one transaction the recorder opened");
        capture.Commits.Should().Be(1);
    }

    [Test]
    public async Task InsideTheCallersTransaction_NoOtherIsOpened_AndNoEventFollowsAChangeThatTouchedNothing()
    {
        var capture = new SqlCapture().ThenRowsAffected(0);
        var factory = capture.Factory();
        var recorder = RecorderOver(factory, new LogRecorder());
        await using var context = await factory.CreateDbContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();

        var rows = await recorder.SaveWithEventsAsync(
            context,
            () => context.ProjectMembers
                .Where(x => x.ProjectId == 4)
                .ExecuteDeleteAsync(),
            x => x > 0 ? [new SecurityAuditEntry(SecurityAuditActions.MemberRemoved).On(SecurityAuditResources.Project, 4)] : [],
            CancellationToken.None);

        rows.Should().Be(0);
        capture.TransactionIsolationLevels.Should().ContainSingle("the caller's transaction is the only one");
        capture.Commands.Should().ContainSingle().Which.Should().StartWith("DELETE");
    }

    [Test]
    public async Task AWriteOnItsOwn_ThatFails_NeverThrows_AndIsLoggedAtErrorAndCounted()
    {
        var logs = new LogRecorder();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the database is down"));
        var failures = new List<(long Value, string? Action)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name == BeaconTelemetry.SecurityAuditWriteFailuresInstrument)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var action = tags.ToArray().FirstOrDefault(x => x.Key == BeaconTelemetry.SecurityAuditActionTag).Value as string;
            if (action == SecurityAuditActions.SetupTokenRefused)
            {
                failures.Add((value, action));
            }
        });
        listener.Start();

        var act = () => RecorderOver(factory.Object, logs).RecordAsync(new SecurityAuditEntry(SecurityAuditActions.SetupTokenRefused), CancellationToken.None);

        await act.Should().NotThrowAsync();
        var error = logs.Entries.Should().ContainSingle(x => x.EventId.Id == SecurityAuditLogEvents.WriteFailed.Id).Subject;
        error.Level.Should().Be(LogLevel.Error);
        error.Category.Should().Be(BeaconTelemetry.AuditLogCategory);
        error.Message.Should().Contain(SecurityAuditActions.SetupTokenRefused).And.NotContain("database is down");
        failures.Should().ContainSingle().Which.Value.Should().Be(1);
    }

    [Test]
    public async Task AWriteOnItsOwn_IsNotCancelledWithTheRequest()
    {
        var capture = new SqlCapture().ThenRow(1);
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        await RecorderOver(capture.Factory(), new LogRecorder()).RecordAsync(
            new SecurityAuditEntry(SecurityAuditActions.PasswordSignInFailed).By(SecurityAuditActor.Anonymous),
            aborted.Token);

        capture.Commands.Should().ContainSingle().Which.Should().Contain("INSERT INTO beacon.security_audit_events");
    }

    [Test]
    public void AnActionOutsideTheCatalogue_IsRefused()
    {
        var act = () => new SecurityAuditEntry("auth.something_else");

        act.Should().Throw<ArgumentException>();
        SecurityAuditActions.All.Should().OnlyContain(x => x.StartsWith(SecurityAuditActions.CategoryOf(x) + ".", StringComparison.Ordinal) && x.Length <= 64);
        SecurityAuditActions.All
            .Select(SecurityAuditActions.CategoryOf)
            .Should().OnlyContain(x => x.Length <= 32);
    }

    private static Recorder RecorderOver(IDbContextFactory<BeaconContext> factory, LogRecorder logs)
    {
        return new Recorder(
            factory,
            TestProjectAccess.Actors(3, "ext-ada"),
            SecurityAuditTestKit.Integrity,
            SecurityAuditTestKit.CallerHasher,
            TimeProvider.System,
            LoggerFactory.Create(x => x.AddProvider(logs)),
            new HttpContextAccessor());
    }
}
