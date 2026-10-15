using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Handlers.SecurityAudit;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Telemetry;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.SecurityAudit;

/// <summary>
/// The Auditor's reads of the security audit trail and its retention: the paged read filters by time window (at most 93
/// days), category, action, actor, resource and project on both providers and is itself recorded with the filters' shape
/// only; verification reports changed, forged and unknown-key events and is recorded; the retention purge does nothing
/// while no retention is set, and otherwise deletes the expired events in one statement and records that it did.
/// </summary>
[TestFixture]
public class SecurityAuditReadTests
{
    private static readonly DateTime From = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc);

    [TestCase(false, "FROM beacon.security_audit_events", "s.created_time >= @", "s.category = @", "s.action = @", "s.actor_user_id = @", "s.resource_type = @", "s.resource_id = @", "s.project_id = @")]
    [TestCase(true, "FROM [beacon].[SecurityAuditEvents]", "[s].[CreatedTime] >= @", "[s].[Category] = @", "[s].[Action] = @", "[s].[ActorUserId] = @", "[s].[ResourceType] = @", "[s].[ResourceId] = @", "[s].[ProjectId] = @")]
    public async Task TheRead_FiltersEveryGivenField_AsParameters_OnBothProviders(bool sqlServer, params string[] expected)
    {
        var capture = (sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture()).AnsweringCountsWithOne();
        var audit = new RecordingSecurityAudit(Auditor());

        await Handler(capture.Factory(), audit, new LogRecorder()).Handle(
            new GetSecurityAuditEventsQuery
            {
                From = From,
                To = To,
                Category = SecurityAuditCategories.Identity,
                Action = SecurityAuditActions.UserCreated,
                ActorUserId = 3,
                ResourceType = SecurityAuditResources.User,
                ResourceId = "7",
                ProjectId = 2
            },
            CancellationToken.None);

        capture.Commands.Should().HaveCount(2, "a count, then the page");
        foreach (var sql in capture.Commands)
        {
            foreach (var fragment in expected)
            {
                sql.Should().Contain(fragment);
            }

            sql.Should().NotContain("identity.user_created", "filter values are parameters");
        }
    }

    [Test]
    public async Task TheRead_WithoutFilters_ReadsOnlyTheWindow_NewestFirst()
    {
        var capture = new SqlCapture().AnsweringCountsWithOne();

        await Handler(capture.Factory(), new RecordingSecurityAudit(Auditor()), new LogRecorder())
            .Handle(new GetSecurityAuditEventsQuery { From = From, To = To }, CancellationToken.None);

        var page = capture.Commands[1];
        page.Should().Contain("s.created_time >= @").And.Contain("s.created_time <= @").And.Contain("ORDER BY s.created_time DESC");
        page.Should().NotContain("s.category =").And.NotContain("s.actor_user_id =").And.NotContain("s.project_id =");
    }

    [Test]
    public async Task TheRead_ReturnsEveryFieldOfEachEventOfTheWindow_AsStored_NewestFirst()
    {
        var older = Core.SecurityAudit.SecurityAuditRecorder.ToEvent(
            new SecurityAuditEntry(SecurityAuditActions.ApiKeyRevoked)
                .On(SecurityAuditResources.ApiKey, 5)
                .InProject(2)
                .Ending(SecurityAuditOutcomes.Refused)
                .With(new SecurityAuditDetails().AddCode("reason", "owner_inactive")),
            new SecurityAuditActor(SecurityAuditActorKinds.ApiKey, 3, 5, "ab12cd34"),
            From.AddDays(1),
            "0af7651916cd43dd8448eb211c80319c",
            SecurityAuditTestKit.Integrity);
        older.Id = 1;
        var newer = Sealed(2, SecurityAuditTestKit.Integrity);
        newer.CreatedTime = From.AddDays(2);
        var outside = Sealed(3, SecurityAuditTestKit.Integrity);
        outside.CreatedTime = From.AddTicks(-10);
        var saved = new List<object>();
        var rows = new List<SecurityAuditEvent> { older, newer, outside };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(new Dictionary<Type, object> { [typeof(SecurityAuditEvent)] = RecordingBeaconContext.MemorySet(rows, saved) }, saved));

        var page = await Handler(factory.Object, new RecordingSecurityAudit(Auditor()), new LogRecorder())
            .Handle(new GetSecurityAuditEventsQuery { From = From, To = To }, CancellationToken.None);

        page.TotalCount.Should().Be(2, "the event before the window is not read");
        page.Items.Select(x => x.Id).Should().Equal(2, 1);
        var item = page.Items[1];
        foreach (var field in typeof(SecurityAuditEventItem).GetProperties())
        {
            field.GetValue(item).Should().Be(typeof(SecurityAuditEvent).GetProperty(field.Name)!.GetValue(older), $"{field.Name} is returned as stored");
        }

        item.ApiKeyId.Should().Be(5);
        item.CallerHash.Should().Be("ab12cd34");
        item.TraceId.Should().Be("0af7651916cd43dd8448eb211c80319c");
        item.DetailsJson.Should().Be("{\"reason\":\"owner_inactive\"}");
    }

    [Test]
    public async Task EveryRead_IsRecorded_WithTheFiltersShapeOnly()
    {
        var capture = new SqlCapture().AnsweringCountsWithOne();
        var audit = new RecordingSecurityAudit(Auditor());
        var logs = new LogRecorder();

        await Handler(capture.Factory(), audit, logs).Handle(
            new GetSecurityAuditEventsQuery { From = From, To = To, Category = "identity'; select 1 --", ResourceId = "ext-secret-id", ActorUserId = 3 },
            CancellationToken.None);

        var read = audit.Single("ext-secret-id", "select 1");
        read.Action.Should().Be(SecurityAuditActions.AuditRead);
        read.ActorUserId.Should().Be(9);
        read.ResourceType.Should().Be(SecurityAuditResources.SecurityAudit);
        read.DetailsJson.Should().Contain("\"category\":\"[withheld]\"").And.Contain("\"hasResourceId\":true").And.Contain("\"actorUserId\":3");
        var logged = logs.Entries.Should().ContainSingle(x => x.EventId.Id == SecurityAuditLogEvents.Read.Id).Subject;
        logged.Category.Should().Be(BeaconTelemetry.AuditLogCategory);
        logged.Message.Should().NotContain("select 1").And.NotContain("ext-secret-id").And.Contain("HasCategoryFilter=True");
    }

    [TestCase(94)]
    [TestCase(-1)]
    public async Task AWindowOverNinetyThreeDays_OrBackwards_IsRefused_AndNothingIsRead(int days)
    {
        var capture = new SqlCapture();
        var audit = new RecordingSecurityAudit(Auditor());

        var act = () => Handler(capture.Factory(), audit, new LogRecorder())
            .Handle(new GetSecurityAuditEventsQuery { From = From, To = From.AddDays(days) }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        capture.Commands.Should().BeEmpty();
        audit.Events.Should().BeEmpty();
    }

    [Test]
    public async Task AReadWithoutAWindow_IsRefused()
    {
        var act = () => Handler(new SqlCapture().Factory(), new RecordingSecurityAudit(), new LogRecorder())
            .Handle(new GetSecurityAuditEventsQuery { To = To }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*required*");
    }

    [Test]
    public async Task Verification_FindsChangedForgedAndUnknownKeyEvents_AndIsRecorded()
    {
        var intact = Sealed(1, SecurityAuditTestKit.Integrity);
        var changed = Sealed(2, SecurityAuditTestKit.Integrity);
        changed.ActorUserId = 1;
        var forged = Sealed(3, SecurityAuditTestKit.Integrity);
        forged.Hmac = new string('a', 64);
        var unknownKey = Sealed(4, new SecurityAuditIntegrity("a-key-this-deployment-does-not-hold-0123456789", []));
        var saved = new List<object>();
        var rows = new List<SecurityAuditEvent> { intact, changed, forged, unknownKey };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(new Dictionary<Type, object> { [typeof(SecurityAuditEvent)] = RecordingBeaconContext.MemorySet(rows, saved) }, saved));
        var audit = new RecordingSecurityAudit(Auditor());

        var result = await VerifyHandler(factory.Object, audit)
            .Handle(new VerifySecurityAuditCommand(From, To), CancellationToken.None);

        result.Should().BeEquivalentTo(new VerifySecurityAuditResult(4, 1, 2, 1, [2, 3, 4]) { Outcome = SecurityAuditOutcomes.Failure });
        var verified = audit.Single();
        verified.Action.Should().Be(SecurityAuditActions.AuditVerified);
        verified.Outcome.Should().Be(SecurityAuditOutcomes.Failure);
        verified.DetailsJson.Should().Contain("\"failedEventIds\":[2,3,4]");
    }

    [Test]
    public async Task Verification_OfGivenEvents_ChecksOnlyThoseEventsOfTheWindow_AndRecordsHowManyWereAskedFor()
    {
        var intact = Sealed(1, SecurityAuditTestKit.Integrity);
        var changed = Sealed(2, SecurityAuditTestKit.Integrity);
        changed.ActorUserId = 1;
        var alsoIntact = Sealed(3, SecurityAuditTestKit.Integrity);
        var forged = Sealed(4, SecurityAuditTestKit.Integrity);
        forged.Hmac = new string('a', 64);
        var audit = new RecordingSecurityAudit(Auditor());

        var result = await VerifyHandler([intact, changed, alsoIntact, forged], audit)
            .Handle(new VerifySecurityAuditCommand(From, To, [3, 2, 3]), CancellationToken.None);

        result.Should().BeEquivalentTo(
            new VerifySecurityAuditResult(2, 1, 1, 0, [2])
            {
                Outcome = SecurityAuditOutcomes.Failure,
                Verdicts = [new SecurityAuditEventVerdict(3, SecurityAuditVerdicts.Intact), new SecurityAuditEventVerdict(2, SecurityAuditVerdicts.Modified)]
            },
            "event 4 is outside the ids asked for");
        audit.Single().DetailsJson.Should().Contain("\"requestedEventCount\":2");
    }

    [Test]
    public async Task Verification_OfMoreThanAPageOfEvents_IsRefused_AndNothingIsRead()
    {
        var capture = new SqlCapture();
        var audit = new RecordingSecurityAudit(Auditor());
        var ids = Enumerable.Range(1, VerifySecurityAuditHandler.MaxEventIds + 1).ToList();

        var act = () => VerifyHandler(capture.Factory(), audit)
            .Handle(new VerifySecurityAuditCommand(From, To, ids), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        capture.Commands.Should().BeEmpty();
        audit.Events.Should().BeEmpty();
    }

    [TestCase(false, "s.id = ANY (@")]
    [TestCase(true, "[s].[Id] IN (")]
    public async Task Verification_OfGivenEvents_FiltersByTheIds_AsParameters_OnBothProviders(bool sqlServer, string fragment)
    {
        var capture = (sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture()).AnsweringCountsWithOne();

        await VerifyHandler(capture.Factory(), new RecordingSecurityAudit(Auditor()))
            .Handle(new VerifySecurityAuditCommand(From, To, [5, 6]), CancellationToken.None);

        capture.Commands.Should().ContainSingle().Which.Should().Contain(fragment);
    }

    [Test]
    public async Task WithoutARetention_ThePurgeDeletesNothing_AndRecordsNothing()
    {
        var capture = new SqlCapture();
        var audit = new RecordingSecurityAudit();

        var deleted = await Retention(capture.Factory(), audit, retentionDays: null).PurgeExpiredAsync(CancellationToken.None);

        deleted.Should().Be(0);
        capture.Commands.Should().BeEmpty();
        audit.Events.Should().BeEmpty();
    }

    [TestCase(false, "DELETE FROM beacon.security_audit_events AS s", "s.created_time < @")]
    [TestCase(true, "DELETE FROM [s]", "[s].[CreatedTime] < @")]
    public async Task WithARetention_ThePurgeDeletesTheExpiredEventsInOneStatement_AndRecordsIt(bool sqlServer, string delete, string predicate)
    {
        var capture = (sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture()).ThenNoRows().ThenRowsAffected(3);
        var audit = new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest);
        var logs = new LogRecorder();

        var deleted = await Retention(capture.Factory(), audit, retentionDays: 400, logs).PurgeExpiredAsync(CancellationToken.None);

        deleted.Should().Be(3);
        capture.Commands.Should().HaveCount(2, "the expired events are verified first, then deleted in one statement");
        capture.Commands[1].Should().Contain(delete).And.Contain(predicate);
        capture.CommandParameters[1].Values.OfType<DateTime>().Should().ContainSingle()
            .Which.Should().Be(new DateTime(2026, 10, 14, 4, 0, 0, DateTimeKind.Utc).AddDays(-400));
        var purged = audit.Single();
        purged.Action.Should().Be(SecurityAuditActions.AuditPurged);
        purged.ActorKind.Should().Be(SecurityAuditActorKinds.System);
        purged.DetailsJson.Should().Contain("\"deletedCount\":3").And.Contain("\"retentionDays\":400");
        logs.Entries.Should().ContainSingle(x => x.EventId.Id == SecurityAuditLogEvents.Purged.Id);
    }

    private static Core.Authorization.BeaconActor Auditor()
    {
        return new Core.Authorization.BeaconActor("ext-auditor", false) { Kind = Core.Authorization.BeaconPrincipalKind.User, BeaconUserId = 9, IsActiveUser = true };
    }

    private static VerifySecurityAuditHandler VerifyHandler(List<SecurityAuditEvent> rows, ISecurityAuditRecorder audit)
    {
        var saved = new List<object>();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(new Dictionary<Type, object> { [typeof(SecurityAuditEvent)] = RecordingBeaconContext.MemorySet(rows, saved) }, saved));

        return VerifyHandler(factory.Object, audit);
    }

    private static VerifySecurityAuditHandler VerifyHandler(IDbContextFactory<BeaconContext> factory, ISecurityAuditRecorder audit)
    {
        return new VerifySecurityAuditHandler(
            factory,
            SecurityAuditTestKit.Integrity,
            SecurityAuditTestKit.Reads(factory, audit, Auditor()),
            new SecurityAuditReadGate(TimeProvider.System),
            Options.Create(new SecurityAuditOptions()),
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 4, 0, 0, TimeSpan.Zero)),
            LoggerFactory.Create(x => x.AddProvider(new LogRecorder())));
    }

    private static GetSecurityAuditEventsHandler Handler(IDbContextFactory<BeaconContext> factory, ISecurityAuditRecorder audit, LogRecorder logs)
    {
        return new GetSecurityAuditEventsHandler(factory, SecurityAuditTestKit.Reads(factory, audit, Auditor()), LoggerFactory.Create(x => x.AddProvider(logs)));
    }

    private static ISecurityAuditRetentionService Retention(IDbContextFactory<BeaconContext> factory, ISecurityAuditRecorder audit, int? retentionDays, LogRecorder? logs = null)
    {
        return new SecurityAuditRetentionService(
            factory,
            Options.Create(new SecurityAuditOptions { RetentionDays = retentionDays }),
            audit,
            SecurityAuditTestKit.Integrity,
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 4, 0, 0, TimeSpan.Zero)),
            LoggerFactory.Create(x => x.AddProvider(logs ?? new LogRecorder())));
    }

    private static SecurityAuditEvent Sealed(int id, SecurityAuditIntegrity integrity)
    {
        var auditEvent = Core.SecurityAudit.SecurityAuditRecorder.ToEvent(
            new SecurityAuditEntry(SecurityAuditActions.UserDisabled).On(SecurityAuditResources.User, 7),
            SecurityAuditActor.OfUser(3),
            From.AddDays(1),
            null,
            integrity);
        auditEvent.Id = id;

        return auditEvent;
    }
}
