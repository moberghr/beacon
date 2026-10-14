using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using Beacon.Api;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Handlers.SecurityAudit;
using Beacon.Core.Models;
using Beacon.Core.SecurityAudit;
using Beacon.Tests.Common;
using Recorder = Beacon.Core.SecurityAudit.SecurityAuditRecorder;

namespace Beacon.Tests.Unit.SecurityAudit;

/// <summary>
/// How the trail is verified and read: a verification by id answers every id it was asked for (an event never counts as
/// intact without being checked; one dated where no genuine event can be is out of range and not found), even past the
/// first failing ids it lists; a verification counts only the events of its window, read in id-ordered batches to the last
/// (on both providers); a verification that checks nothing is no success; one runs at a time; reads and verifications are
/// recorded before anything is returned and fail when they cannot be, an identical one by the same actor within a minute
/// recorded once. The scheduled verification continues from its last intact checkpoint, reports failures at Error and
/// writes a checkpoint each run; the retention purge verifies what it is about to delete and refuses while any of it fails;
/// the sample host's jobs run both, a refused purge failing its job. The startup note names the integrity key by id and
/// source.
/// </summary>
[TestFixture]
public class SecurityAuditVerificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 4, 0, 0, TimeSpan.Zero);
    private static readonly DateTime From = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task AVerificationById_AnswersEveryRequestedId_AndNoneIsIntactWithoutBeingChecked()
    {
        var changed = Sealed(2, Now.UtcDateTime.AddDays(-2));
        changed.ActorUserId = 1;
        var futureDated = Sealed(5, Now.UtcDateTime.AddDays(2));
        var rows = new List<SecurityAuditEvent>
        {
            Sealed(1, Now.UtcDateTime.AddDays(-3)),
            changed,
            Sealed(3, Now.UtcDateTime.AddDays(-1), new SecurityAuditIntegrity("a-key-this-deployment-does-not-hold-0123456789", [])),
            futureDated
        };
        var audit = new RecordingSecurityAudit(Auditor());

        var result = await VerifyHandler(rows, audit).Handle(new VerifySecurityAuditCommand(From, To, [1, 2, 3, 99, 5]), CancellationToken.None);

        result.Verdicts.Should().Equal(
            new SecurityAuditEventVerdict(1, SecurityAuditVerdicts.Intact),
            new SecurityAuditEventVerdict(2, SecurityAuditVerdicts.Modified),
            new SecurityAuditEventVerdict(3, SecurityAuditVerdicts.UnknownKey),
            new SecurityAuditEventVerdict(99, SecurityAuditVerdicts.NotFound),
            new SecurityAuditEventVerdict(5, SecurityAuditVerdicts.NotFound));
        result.Should().BeEquivalentTo(new { CheckedCount = 3, IntactCount = 1, ModifiedCount = 1, UnknownKeyCount = 1, OutOfRangeCount = 1, Outcome = SecurityAuditOutcomes.Failure });
        result.FailedEventIds.Should().Equal(2, 3, 5);
        var verified = audit.Single();
        verified.Outcome.Should().Be(SecurityAuditOutcomes.Failure);
        verified.DetailsJson.Should().Contain("\"outOfRangeCount\":1").And.Contain("\"notFoundCount\":2");
    }

    [Test]
    public async Task AnEventOlderThanTheRetentionAllows_IsOutOfRange_AndAWindowWithIt_FailsVerification()
    {
        var rows = new List<SecurityAuditEvent> { Sealed(1, Now.UtcDateTime.AddDays(-400)), Sealed(2, Now.UtcDateTime.AddDays(-1)) };

        var result = await VerifyHandler(rows, new RecordingSecurityAudit(Auditor()), retentionDays: 365)
            .Handle(new VerifySecurityAuditCommand(Now.UtcDateTime.AddDays(-401), Now.UtcDateTime.AddDays(-320)), CancellationToken.None);

        result.Should().BeEquivalentTo(new { CheckedCount = 0, OutOfRangeCount = 1, Outcome = SecurityAuditOutcomes.Failure, Verdicts = (List<SecurityAuditEventVerdict>?)null });
        result.FailedEventIds.Should().Equal(1);
    }

    [Test]
    public async Task AVerificationThatChecksNothing_IsNoSuccess()
    {
        var audit = new RecordingSecurityAudit(Auditor());

        var result = await VerifyHandler([], audit).Handle(new VerifySecurityAuditCommand(From, To), CancellationToken.None);

        result.Outcome.Should().Be(SecurityAuditOutcomes.Empty);
        audit.Single().Outcome.Should().Be(SecurityAuditOutcomes.Empty);
    }

    // The events just outside the window are changed: counting either of them would fail the verification.
    [Test]
    public async Task AVerification_CountsOnlyTheEventsOfItsWindow_BothEndsIncluded()
    {
        var from = Now.UtcDateTime.AddDays(-10);
        var to = Now.UtcDateTime.AddDays(-2);
        var before = Sealed(1, from.AddTicks(-10));
        before.ActorUserId = 99;
        var after = Sealed(4, to.AddTicks(10));
        after.ActorUserId = 99;
        var rows = new List<SecurityAuditEvent> { before, Sealed(2, from), Sealed(3, to), after };

        var window = await VerifyHandler(rows, new RecordingSecurityAudit(Auditor())).Handle(new VerifySecurityAuditCommand(from, to), CancellationToken.None);
        var byId = await VerifyHandler(rows, new RecordingSecurityAudit(Auditor())).Handle(new VerifySecurityAuditCommand(from, to, [1, 2, 3, 4]), CancellationToken.None);

        window.Should().BeEquivalentTo(new { CheckedCount = 2, IntactCount = 2, ModifiedCount = 0, OutOfRangeCount = 0, Outcome = SecurityAuditOutcomes.Success });
        window.FailedEventIds.Should().BeEmpty();
        byId.Verdicts.Should().Equal(
            new SecurityAuditEventVerdict(1, SecurityAuditVerdicts.NotFound),
            new SecurityAuditEventVerdict(2, SecurityAuditVerdicts.Intact),
            new SecurityAuditEventVerdict(3, SecurityAuditVerdicts.Intact),
            new SecurityAuditEventVerdict(4, SecurityAuditVerdicts.NotFound));
    }

    [TestCase(false, "FROM beacon.security_audit_events", "s.created_time >= @", "s.created_time <= @", "s.id > @", "ORDER BY s.id", "LIMIT ")]
    [TestCase(true, "FROM [beacon].[SecurityAuditEvents]", "[s].[CreatedTime] >= @", "[s].[CreatedTime] <= @", "[s].[Id] > @", "ORDER BY [s].[Id]", "TOP(")]
    public async Task AVerification_ReadsItsWindowInIdOrderedBatches_AsParameters_Translates(bool sqlServer, params string[] expected)
    {
        var capture = (sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture()).ThenNoRows();

        await VerifyHandler(capture.Factory(), new RecordingSecurityAudit(Auditor())).Handle(new VerifySecurityAuditCommand(From, To), CancellationToken.None);

        var sql = capture.Commands.Should().ContainSingle("an empty first batch ends the read").Subject;
        foreach (var fragment in expected)
        {
            sql.Should().Contain(fragment);
        }

        capture.CommandParameters[0].Values.OfType<DateTime>().Should().BeEquivalentTo([From, To]);
    }

    [Test]
    public async Task AWindowOfMoreThanABatch_IsReadBatchByBatch_ToItsLastEvent()
    {
        var rows = Enumerable.Range(1, (2 * VerifySecurityAuditHandler.BatchSize) + 500)
            .Select(x => Sealed(x, Now.UtcDateTime.AddDays(-1)))
            .ToList();
        rows[^1].ActorUserId = 99;
        rows[VerifySecurityAuditHandler.BatchSize].ActorUserId = 99;

        var result = await VerifyHandler(rows, new RecordingSecurityAudit(Auditor())).Handle(new VerifySecurityAuditCommand(From, To), CancellationToken.None);

        result.CheckedCount.Should().Be(rows.Count, "every batch is read, not only the first");
        result.ModifiedCount.Should().Be(2);
        result.FailedEventIds.Should().Equal(VerifySecurityAuditHandler.BatchSize + 1, rows.Count);
    }

    [Test]
    public async Task MoreFailuresThanAreListed_AreCounted_AndAVerificationById_StillGivesEveryIdItsVerdict()
    {
        var rows = Enumerable.Range(1, VerifySecurityAuditHandler.MaxEventIds)
            .Select(x => Sealed(x, Now.UtcDateTime.AddDays(-1)))
            .ToList();
        foreach (var row in rows)
        {
            row.ResourceId = "changed";
        }

        var ids = rows
            .Select(x => x.Id)
            .Reverse()
            .ToList();
        var audit = new RecordingSecurityAudit(Auditor());

        var window = await VerifyHandler(rows, new RecordingSecurityAudit(Auditor())).Handle(new VerifySecurityAuditCommand(From, To), CancellationToken.None);
        var byId = await VerifyHandler(rows, audit).Handle(new VerifySecurityAuditCommand(From, To, ids), CancellationToken.None);

        foreach (var result in new[] { window, byId })
        {
            result.ModifiedCount.Should().Be(rows.Count);
            result.FailedEventIds.Should().Equal(Enumerable.Range(1, VerifySecurityAuditHandler.MaxReportedEvents), "the first failing ids are listed; the count says how many failed");
        }

        window.Verdicts.Should().BeNull("a window is answered with counts");
        byId.Verdicts!.Select(x => x.EventId).Should().Equal(ids, "every requested id is answered, in the order asked");
        byId.Verdicts.Should().OnlyContain(x => x.Verdict == SecurityAuditVerdicts.Modified, "an id past the listed ones is not taken for intact");
        audit.Single().DetailsJson.Should().Contain($"\"modifiedCount\":{rows.Count}").And.Contain($"\"failedEventIdsCount\":{VerifySecurityAuditHandler.MaxReportedEvents}");
    }

    [Test]
    public async Task WhileAVerificationRuns_AnotherIsRefused_AndReadsNothing()
    {
        var gate = new SecurityAuditReadGate(TimeProvider.System);
        gate.TryStartVerification().Should().BeTrue();
        var capture = new SqlCapture();
        var audit = new RecordingSecurityAudit(Auditor());

        var act = () => VerifyHandler(capture.Factory(), audit, gate).Handle(new VerifySecurityAuditCommand(From, To), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconRateLimitedException>();
        capture.Commands.Should().BeEmpty();
        audit.Events.Should().BeEmpty();
        gate.EndVerification();
    }

    [Test]
    public async Task AVerificationThatCannotBeRecorded_Fails_AndFreesTheVerificationForTheNext()
    {
        var gate = new SecurityAuditReadGate(TimeProvider.System);
        var failing = new Mock<ISecurityAuditRecorder>();
        failing
            .Setup(x => x.StageAsync(It.IsAny<BeaconContext>(), It.IsAny<SecurityAuditEntry>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the database is down"));
        var rows = new List<SecurityAuditEvent> { Sealed(1, Now.UtcDateTime.AddDays(-1)) };

        var act = () => VerifyHandler(rows, failing.Object, gate: gate).Handle(new VerifySecurityAuditCommand(From, To), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        gate.TryStartVerification().Should().BeTrue("a failed verification never keeps the slot");
        gate.EndVerification();
    }

    [Test]
    public async Task AReadThatCannotBeRecorded_ReadsNothing()
    {
        var capture = new SqlCapture();
        var failing = new Mock<ISecurityAuditRecorder>();
        failing
            .Setup(x => x.StageAsync(It.IsAny<BeaconContext>(), It.IsAny<SecurityAuditEntry>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the database is down"));
        var handler = new GetSecurityAuditEventsHandler(capture.Factory(), SecurityAuditTestKit.Reads(capture.Factory(), failing.Object), NullLoggerFactory.Instance);

        var act = () => handler.Handle(new GetSecurityAuditEventsQuery { From = From, To = To }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        capture.Commands.Should().BeEmpty("the trail is read only once the read is on record");
    }

    [Test]
    public async Task AnIdenticalRead_BySameActorWithinAMinute_IsRecordedOnce_AndAgainAfterIt()
    {
        var time = new FakeTimeProvider(Now);
        var gate = new SecurityAuditReadGate(time);
        var capture = new SqlCapture().AnsweringCountsWithOne();
        var audit = new RecordingSecurityAudit(Auditor());
        var other = new RecordingSecurityAudit(Auditor());
        var handler = new GetSecurityAuditEventsHandler(capture.Factory(), SecurityAuditTestKit.Reads(capture.Factory(), audit, Auditor(), gate), NullLoggerFactory.Instance);
        var query = new GetSecurityAuditEventsQuery { From = From, To = To, Category = SecurityAuditCategories.Auth };

        await handler.Handle(query, CancellationToken.None);
        await handler.Handle(query, CancellationToken.None);
        await handler.Handle(query with { Page = 1 }, CancellationToken.None);
        await new GetSecurityAuditEventsHandler(capture.Factory(), SecurityAuditTestKit.Reads(capture.Factory(), other, RecordingSecurityAudit.User(10, "ext-other"), gate), NullLoggerFactory.Instance)
            .Handle(query, CancellationToken.None);
        time.Advance(SecurityAuditReadGate.CollapseWindow);
        await handler.Handle(query, CancellationToken.None);

        audit.Of(SecurityAuditActions.AuditRead).Should().HaveCount(3, "the repeat within the minute is the same read; another page and a later read are not");
        other.Of(SecurityAuditActions.AuditRead).Should().ContainSingle("another actor's identical read is its own");
    }

    [Test]
    public async Task ThePurge_RefusesWhileAnEventItWouldDeleteFailsVerification_AndDeletesNothing()
    {
        var expired = Sealed(1, Now.UtcDateTime.AddDays(-500));
        var tampered = Sealed(2, Now.UtcDateTime.AddDays(-450));
        tampered.Outcome = SecurityAuditOutcomes.Success + "x";
        var rows = new List<SecurityAuditEvent> { expired, tampered, Sealed(3, Now.UtcDateTime.AddDays(-1)) };
        var audit = new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest);
        var logs = new LogRecorder();

        var act = () => Retention(Factory(rows), audit, logs).PurgeExpiredAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*refused*");
        rows.Should().HaveCount(3, "nothing is deleted while an event it would delete fails");
        var error = logs.Entries.Should().ContainSingle(x => x.EventId.Id == SecurityAuditLogEvents.VerificationFailed.Id).Subject;
        error.Level.Should().Be(LogLevel.Error);
        error.Message.Should().Contain("first ids 2");
        var refused = audit.Single();
        refused.Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.AuditPurged, Outcome = SecurityAuditOutcomes.Refused, ActorKind = SecurityAuditActorKinds.System },
            x => x.ExcludingMissingMembers());
        refused.DetailsJson.Should().Contain("\"failedEventIds\":[2]").And.Contain("\"deletedCount\":0");
        logs.Entries.Should().NotContain(x => x.EventId.Id == SecurityAuditLogEvents.Purged.Id);
    }

    [Test]
    public async Task ThePurge_DeletesTheExpiredEvents_WithItsEvent_InOneUnitOfWork()
    {
        var rows = new List<SecurityAuditEvent> { Sealed(1, Now.UtcDateTime.AddDays(-500)), Sealed(2, Now.UtcDateTime.AddDays(-1)) };
        var audit = new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest);

        var deleted = await Retention(Factory(rows), audit, new LogRecorder()).PurgeExpiredAsync(CancellationToken.None);

        deleted.Should().Be(1);
        rows.Select(x => x.Id).Should().Equal(2);
        audit.Ways.Should().Equal("saved");
        audit.Single().ActorKind.Should().Be(SecurityAuditActorKinds.System);
    }

    [Test]
    public async Task APurgeThatDoesNotRun_IsLoggedAtError_AndFailsTheJob()
    {
        var logs = new LogRecorder();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the database is down"));

        var act = () => Retention(factory.Object, new RecordingSecurityAudit(), logs).PurgeExpiredAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        var error = logs.Entries.Should().ContainSingle(x => x.EventId.Id == SecurityAuditLogEvents.PurgeFailed.Id).Subject;
        error.Level.Should().Be(LogLevel.Error);
        error.Message.Should().NotContain("the database is down");
    }

    [Test]
    public async Task TheScheduledVerification_ChecksTheSettledEvents_ReportsFailuresAtError_AndWritesACheckpoint()
    {
        var changed = Sealed(2, Now.UtcDateTime.AddHours(-3));
        changed.ResourceId = "8";
        var unsettled = Sealed(4, Now.UtcDateTime.AddMinutes(-1));
        var rows = new List<SecurityAuditEvent> { Sealed(1, Now.UtcDateTime.AddHours(-4)), changed, Sealed(3, Now.UtcDateTime.AddHours(-2)), unsettled };
        var audit = new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest);
        var logs = new LogRecorder();

        var checkpoint = await Verification(rows, audit, logs).VerifyNewEventsAsync(CancellationToken.None);

        checkpoint.Should().BeEquivalentTo(new SecurityAuditCheckpoint(0, 3, rows[2].Hmac, 3, 1, 0, 0, [2]), "an event younger than the settle time waits for the next run");
        var failure = logs.Entries.Should().ContainSingle(x => x.EventId.Id == SecurityAuditLogEvents.VerificationFailed.Id).Subject;
        failure.Level.Should().Be(LogLevel.Error);
        failure.Message.Should().Contain("first ids 2");
        logs.Entries.Should().ContainSingle(x => x.EventId.Id == SecurityAuditLogEvents.Checkpoint.Id)
            .Which.Message.Should().Contain("MaxEventId=3").And.Contain($"LastHmac={rows[2].Hmac}").And.Contain("Checked=3");
        var verified = audit.Single();
        verified.Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.AuditVerified, Outcome = SecurityAuditOutcomes.Failure, ActorKind = SecurityAuditActorKinds.System },
            x => x.ExcludingMissingMembers());
        verified.DetailsJson.Should().Contain("\"maxEventId\":3").And.Contain("\"failedEventIds\":[2]");
    }

    [Test]
    public async Task TheScheduledVerification_ContinuesFromItsLastIntactCheckpoint_AndPassesOverAForgedOne()
    {
        var old = Sealed(1, Now.UtcDateTime.AddHours(-5));
        old.ActorUserId = 99;
        var checkpoint = Checkpoint(2, maxEventId: 1, Now.UtcDateTime.AddHours(-4));
        var forged = Checkpoint(4, maxEventId: 5, Now.UtcDateTime.AddHours(-2));
        forged.Hmac = new string('a', 64);
        var rows = new List<SecurityAuditEvent> { old, checkpoint, Sealed(3, Now.UtcDateTime.AddHours(-3)), forged, Sealed(5, Now.UtcDateTime.AddHours(-1)) };
        var logs = new LogRecorder();

        var result = await Verification(rows, new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest), logs).VerifyNewEventsAsync(CancellationToken.None);

        result.FromEventId.Should().Be(1, "the forged checkpoint naming event 5 is passed over");
        result.FailedEventIds.Should().Equal([4], "event 1 was checked by an earlier run; the forged checkpoint fails as any event would");
        logs.Entries.Where(x => x.EventId.Id == SecurityAuditLogEvents.VerificationFailed.Id).Should().HaveCount(2, "the forged checkpoint, then the run's failures");
    }

    [Test]
    public async Task TheScheduledVerification_WithNothingNew_RecordsNoEvent_ButStillWritesItsCheckpoint()
    {
        var rows = new List<SecurityAuditEvent> { Checkpoint(7, maxEventId: 6, Now.UtcDateTime.AddHours(-1)) };
        var audit = new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest);
        var logs = new LogRecorder();

        var checkpoint = await Verification(rows, audit, logs).VerifyNewEventsAsync(CancellationToken.None);

        checkpoint.CheckedCount.Should().Be(1, "the checkpoint event itself is new");
        audit.Events.Should().ContainSingle();
        rows.Clear();
        rows.Add(Checkpoint(8, maxEventId: 8, Now.UtcDateTime.AddHours(-1)));

        await Verification(rows, audit, logs).VerifyNewEventsAsync(CancellationToken.None);

        audit.Events.Should().ContainSingle("a run that checked nothing records nothing");
        logs.Entries.Where(x => x.EventId.Id == SecurityAuditLogEvents.Checkpoint.Id).Should().HaveCount(2);
    }

    [TestCase("an-integrity-key-for-this-deployment-only-0123", SecurityAuditKeySources.IntegrityKey, false)]
    [TestCase(null, SecurityAuditKeySources.DerivedFromEncryptionKey, true)]
    public void TheStartupNote_NamesTheIntegrityKeyByIdAndSource_AndWarnsWhenItIsDerived(string? integrityKey, string source, bool warns)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Beacon:Audit:IntegrityKey"] = integrityKey })
            .Build();
        var integrity = SecurityAuditIntegrity.From(configuration, "the-encryption-key-of-this-test-0123456789", []);
        var logs = new LogRecorder();

        new SecurityAuditStartupNote(logs.For<SecurityAuditStartupNote>(), integrity, Options.Create(new SecurityAuditOptions { RetentionDays = 400 })).Note();

        integrity.KeySource.Should().Be(source);
        logs.Entries.Should().Contain(x => x.Level == LogLevel.Information && x.Message.Contains(integrity.CurrentKeyId) && x.Message.Contains(source));
        logs.Entries.Any(x => x.Level == LogLevel.Warning && x.Message.Contains("Beacon:Audit:IntegrityKey")).Should().Be(warns);
        logs.Entries.Should().Contain(x => x.Message.Contains("400 days") && x.Message.Contains("security-audit-retention"));
        logs.Entries.Should().NotContain(x => x.Message.Contains("the-encryption-key-of-this-test") || (integrityKey != null && x.Message.Contains(integrityKey)));
    }

    // The sample host's Warp jobs run the purge and the scheduled verification with the job's token; a purge that refuses
    // or fails fails its job (Warp retries nothing), so the failure stays visible on the dashboard.
    [Test]
    public async Task ThePurgeJob_RunsThePurge_AndARefusedPurgeFailsTheJob()
    {
        using var cancellation = new CancellationTokenSource();
        var retention = new Mock<ISecurityAuditRetentionService>();
        retention
            .SetupSequence(x => x.PurgeExpiredAsync(cancellation.Token))
            .ReturnsAsync(3)
            .ThrowsAsync(new InvalidOperationException("The security audit retention purge was refused."));
        var handler = new Beacon.SampleProject.Warp.Jobs.PurgeExpiredSecurityAuditEventsJobHandler(retention.Object);

        await handler.HandleAsync(new Beacon.SampleProject.Warp.Jobs.PurgeExpiredSecurityAuditEventsJob(), cancellation.Token);
        var refused = () => handler.HandleAsync(new Beacon.SampleProject.Warp.Jobs.PurgeExpiredSecurityAuditEventsJob(), cancellation.Token);

        await refused.Should().ThrowAsync<InvalidOperationException>().WithMessage("*refused*");
        retention.Verify(x => x.PurgeExpiredAsync(cancellation.Token), Times.Exactly(2));
    }

    [Test]
    public async Task TheVerificationJob_RunsTheScheduledVerification()
    {
        using var cancellation = new CancellationTokenSource();
        var verification = new Mock<ISecurityAuditVerificationService>();
        verification
            .Setup(x => x.VerifyNewEventsAsync(cancellation.Token))
            .ReturnsAsync(new SecurityAuditCheckpoint(0, 0, null, 0, 0, 0, 0, []));

        await new Beacon.SampleProject.Warp.Jobs.VerifySecurityAuditEventsJobHandler(verification.Object)
            .HandleAsync(new Beacon.SampleProject.Warp.Jobs.VerifySecurityAuditEventsJob(), cancellation.Token);

        verification.Verify(x => x.VerifyNewEventsAsync(cancellation.Token), Times.Once);
    }

    private static Core.Authorization.BeaconActor Auditor()
    {
        return RecordingSecurityAudit.User(9, "ext-auditor");
    }

    private static SecurityAuditEvent Sealed(int id, DateTime createdTime, SecurityAuditIntegrity? integrity = null)
    {
        var auditEvent = Recorder.ToEvent(
            new SecurityAuditEntry(SecurityAuditActions.UserDisabled).On(SecurityAuditResources.User, 7),
            SecurityAuditActor.OfUser(3),
            createdTime,
            null,
            integrity ?? SecurityAuditTestKit.Integrity);
        auditEvent.Id = id;

        return auditEvent;
    }

    private static SecurityAuditEvent Checkpoint(int id, int maxEventId, DateTime createdTime)
    {
        var auditEvent = Recorder.ToEvent(
            new SecurityAuditEntry(SecurityAuditActions.AuditVerified)
                .On(SecurityAuditResources.SecurityAudit, null)
                .With(new SecurityAuditDetails().Add("scheduled", true).Add("maxEventId", maxEventId)),
            SecurityAuditActor.System,
            createdTime,
            null,
            SecurityAuditTestKit.Integrity);
        auditEvent.Id = id;

        return auditEvent;
    }

    private static IDbContextFactory<BeaconContext> Factory(List<SecurityAuditEvent> rows)
    {
        var saved = new List<object>();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(new Dictionary<Type, object> { [typeof(SecurityAuditEvent)] = RecordingBeaconContext.MemorySet(rows, saved) }, saved));

        return factory.Object;
    }

    private static VerifySecurityAuditHandler VerifyHandler(List<SecurityAuditEvent> rows, ISecurityAuditRecorder audit, int? retentionDays = null, SecurityAuditReadGate? gate = null)
    {
        return VerifyHandler(Factory(rows), audit, gate, retentionDays);
    }

    private static VerifySecurityAuditHandler VerifyHandler(IDbContextFactory<BeaconContext> factory, ISecurityAuditRecorder audit, SecurityAuditReadGate? gate = null, int? retentionDays = null)
    {
        gate ??= new SecurityAuditReadGate(TimeProvider.System);

        return new VerifySecurityAuditHandler(
            factory,
            SecurityAuditTestKit.Integrity,
            SecurityAuditTestKit.Reads(factory, audit, Auditor(), gate),
            gate,
            Options.Create(new SecurityAuditOptions { RetentionDays = retentionDays }),
            new FakeTimeProvider(Now),
            NullLoggerFactory.Instance);
    }

    private static ISecurityAuditRetentionService Retention(IDbContextFactory<BeaconContext> factory, ISecurityAuditRecorder audit, LogRecorder logs)
    {
        return new SecurityAuditRetentionService(
            factory,
            Options.Create(new SecurityAuditOptions { RetentionDays = 400 }),
            audit,
            SecurityAuditTestKit.Integrity,
            new FakeTimeProvider(Now),
            LoggerFactory.Create(x => x.AddProvider(logs)));
    }

    private static ISecurityAuditVerificationService Verification(List<SecurityAuditEvent> rows, ISecurityAuditRecorder audit, LogRecorder logs)
    {
        return new SecurityAuditVerificationService(
            Factory(rows),
            SecurityAuditTestKit.Integrity,
            audit,
            new SecurityAuditReadGate(TimeProvider.System),
            Options.Create(new SecurityAuditOptions()),
            new FakeTimeProvider(Now),
            LoggerFactory.Create(x => x.AddProvider(logs)));
    }
}
