using System.Collections;
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
/// recorded once. The scheduled verification continues from its last intact checkpoint, checks every newer event by id
/// (never selecting by time), waits for a missing id and passes it as never committed after the grace, reports failures at
/// Error and writes a checkpoint each run; the retention purge verifies what it is about to delete, refuses while any of
/// it fails, and deletes only the events it verified, by id and HMAC; the sample host's jobs run both, a refused purge
/// failing its job. The startup note names the integrity key by id and source.
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
    public async Task ThePurge_DeletesExactlyTheExpiredEventsItVerified_WithItsEvent_InOneUnitOfWork()
    {
        var rows = new List<SecurityAuditEvent> { Sealed(1, Now.UtcDateTime.AddDays(-500)), Sealed(2, Now.UtcDateTime.AddDays(-450)), Sealed(3, Now.UtcDateTime.AddDays(-1)) };
        var audit = new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest);
        var logs = new LogRecorder();

        var deleted = await Retention(Factory(rows), audit, logs).PurgeExpiredAsync(CancellationToken.None);

        deleted.Should().Be(2);
        rows.Select(x => x.Id).Should().Equal(3);
        audit.Ways.Should().Equal("saved");
        var purged = audit.Single();
        purged.ActorKind.Should().Be(SecurityAuditActorKinds.System);
        purged.DetailsJson.Should().Contain("\"deletedCount\":2").And.Contain("\"verifiedCount\":2").And.Contain("\"leftCount\":0");
        logs.Entries.Should().ContainSingle(x => x.EventId.Id == SecurityAuditLogEvents.Purged.Id).Which.Level.Should().Be(LogLevel.Information);
    }

    // A writer between the purge's verification and its delete (read committed lets one in): a verified event rewritten
    // with another HMAC is not the event that was verified, and stays for the next purge to verify.
    [Test]
    public async Task AnEventRewrittenAfterThePurgeVerifiedIt_IsNotDeleted_AndIsLeftForTheNextPurge()
    {
        var rows = new List<SecurityAuditEvent> { Sealed(1, Now.UtcDateTime.AddDays(-500)), Sealed(2, Now.UtcDateTime.AddDays(-450)), Sealed(3, Now.UtcDateTime.AddDays(-1)) };
        var recent = rows[2];
        var rewritten = Sealed(2, Now.UtcDateTime.AddDays(-450));
        rewritten.Hmac = new string('b', 64);
        var audit = new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest);
        var logs = new LogRecorder();

        var deleted = await Retention(FactoryWithWriter(rows, () => rows[1] = rewritten), audit, logs).PurgeExpiredAsync(CancellationToken.None);

        deleted.Should().Be(1);
        rows.Should().Equal([rewritten, recent], "only the events still as they were verified are deleted");
        audit.Single().DetailsJson.Should().Contain("\"deletedCount\":1").And.Contain("\"verifiedCount\":2").And.Contain("\"leftCount\":1");
        var purged = logs.Entries.Should().ContainSingle(x => x.EventId.Id == SecurityAuditLogEvents.Purged.Id).Subject;
        purged.Level.Should().Be(LogLevel.Warning);
        purged.Message.Should().Contain("deleted 1 of the 2 events it verified").And.Contain("1 expired events are left");
    }

    // A writer that backdates an event after the purge verified the expired ones: the purge deletes by id and HMAC, never
    // by date, so the backdated event (which no longer verifies) stays.
    [Test]
    public async Task AnEventBackdatedAfterThePurgeVerified_IsNotDeleted_AndIsLeftForTheNextPurge()
    {
        var rows = new List<SecurityAuditEvent> { Sealed(1, Now.UtcDateTime.AddDays(-500)), Sealed(2, Now.UtcDateTime.AddDays(-450)), Sealed(3, Now.UtcDateTime.AddDays(-1)) };
        var backdated = Sealed(3, Now.UtcDateTime.AddDays(-1));
        backdated.CreatedTime = Now.UtcDateTime.AddDays(-450);
        var audit = new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest);

        var deleted = await Retention(FactoryWithWriter(rows, () => rows[2] = backdated), audit, new LogRecorder()).PurgeExpiredAsync(CancellationToken.None);

        deleted.Should().Be(2);
        rows.Should().Equal([backdated], "the backdated event was never verified intact");
        audit.Single().DetailsJson.Should().Contain("\"deletedCount\":2").And.Contain("\"verifiedCount\":2").And.Contain("\"leftCount\":1");
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
    public async Task TheScheduledVerification_ChecksEveryNewEvent_ReportsFailuresAtError_AndWritesACheckpoint()
    {
        var changed = Sealed(2, Now.UtcDateTime.AddHours(-3));
        changed.ResourceId = "8";
        var justWritten = Sealed(4, Now.UtcDateTime.AddMinutes(-1));
        var rows = new List<SecurityAuditEvent> { Sealed(1, Now.UtcDateTime.AddHours(-4)), changed, Sealed(3, Now.UtcDateTime.AddHours(-2)), justWritten };
        var audit = new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest);
        var logs = new LogRecorder();

        var checkpoint = await Verification(rows, audit, logs).VerifyNewEventsAsync(CancellationToken.None);

        checkpoint.Should().BeEquivalentTo(new SecurityAuditCheckpoint(0, 4, rows[3].Hmac, 4, 1, 0, 0, [2]), "an event is checked by id, however recent its time");
        var failure = logs.Entries.Should().ContainSingle(x => x.EventId.Id == SecurityAuditLogEvents.VerificationFailed.Id).Subject;
        failure.Level.Should().Be(LogLevel.Error);
        failure.Message.Should().Contain("first ids 2");
        logs.Entries.Should().ContainSingle(x => x.EventId.Id == SecurityAuditLogEvents.Checkpoint.Id)
            .Which.Message.Should().Contain("MaxEventId=4").And.Contain($"LastHmac={rows[3].Hmac}").And.Contain("Checked=4").And.Contain("NeverCommitted=0");
        var verified = audit.Single();
        verified.Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.AuditVerified, Outcome = SecurityAuditOutcomes.Failure, ActorKind = SecurityAuditActorKinds.System },
            x => x.ExcludingMissingMembers());
        verified.DetailsJson.Should().Contain("\"maxEventId\":4").And.Contain("\"failedEventIds\":[2]");
    }

    // A changed time is a changed event: the scan selects by id, never by time, so an event whose time was moved into the
    // future is checked and found changed; one that verifies but is dated where no genuine event can be is out of range.
    [Test]
    public async Task TheScheduledVerification_ReportsAnEventWhoseTimeWasMovedIntoTheFuture_AsModified_NeverPassingItUnchecked()
    {
        var movedAhead = Sealed(2, Now.UtcDateTime.AddHours(-3));
        movedAhead.CreatedTime = Now.UtcDateTime.AddDays(30);
        var rows = new List<SecurityAuditEvent>
        {
            Sealed(1, Now.UtcDateTime.AddHours(-4)),
            movedAhead,
            Sealed(3, Now.UtcDateTime.AddDays(30)),
            Sealed(4, Now.UtcDateTime.AddHours(-2))
        };
        var audit = new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest);

        var checkpoint = await Verification(rows, audit, new LogRecorder()).VerifyNewEventsAsync(CancellationToken.None);

        checkpoint.Should().BeEquivalentTo(new SecurityAuditCheckpoint(0, 4, rows[3].Hmac, 3, 1, 0, 1, [2, 3]));
        audit.Single().DetailsJson.Should().Contain("\"modifiedCount\":1").And.Contain("\"outOfRangeCount\":1");
    }

    // An id with no event below a stored one may belong to a transaction that has not committed yet: the checkpoint stays
    // below it, and the next run checks the event once it is there.
    [Test]
    public async Task AnEventCommittedAfterAHigherOne_IsCheckedByTheNextRun()
    {
        var rows = new List<SecurityAuditEvent> { Sealed(1, Now.UtcDateTime.AddMinutes(-20)), Sealed(3, Now.UtcDateTime.AddMinutes(-10)) };
        var logs = new LogRecorder();

        var first = await Verification(rows, new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest), logs).VerifyNewEventsAsync(CancellationToken.None);
        var late = Sealed(2, Now.UtcDateTime.AddMinutes(-15));
        late.ResourceId = "8";
        rows.Insert(1, late);
        rows.Add(Checkpoint(4, first.MaxEventId, Now.UtcDateTime.AddMinutes(-5)));
        var second = await Verification(rows, new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest), logs).VerifyNewEventsAsync(CancellationToken.None);

        first.Should().BeEquivalentTo(new { MaxEventId = 1, CheckedCount = 2, WaitingForEventId = (int?)2, NeverCommittedCount = 0 }, "event 3 is checked, but the checkpoint waits for id 2");
        second.Should().BeEquivalentTo(new { FromEventId = 1, MaxEventId = 4, ModifiedCount = 1, FailedEventIds = new[] { 2 }, WaitingForEventId = (int?)null }, "the late event is checked, and found changed");
    }

    [TestCase(30, false, TestName = "AMissingId_IsWaitedFor_WhileTheEventsAboveItAreRecent")]
    [TestCase(90, true, TestName = "AMissingId_IsPassedAsNeverCommitted_OnceTheIntactEventAboveItIsOlderThanTheGrace")]
    public async Task AMissingId_IsWaitedFor_ThenPassedAsNeverCommitted(int minutesOld, bool passed)
    {
        var changedAbove = Sealed(4, Now.UtcDateTime.AddHours(-3));
        changedAbove.ResourceId = "8";
        var rows = new List<SecurityAuditEvent> { Sealed(1, Now.UtcDateTime.AddHours(-3)), changedAbove, Sealed(5, Now.UtcDateTime.AddMinutes(-minutesOld)) };
        var audit = new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest);
        var logs = new LogRecorder();

        var checkpoint = await Verification(rows, audit, logs).VerifyNewEventsAsync(CancellationToken.None);

        // Ids 2 and 3 were handed out before event 5, the nearest intact event above them: event 4 is changed, so the old
        // time it shows is not taken as its own.
        checkpoint.Should().BeEquivalentTo(passed
            ? new { MaxEventId = 5, NeverCommittedCount = 2, FirstNeverCommittedId = (int?)2, LastNeverCommittedId = (int?)3, WaitingForEventId = (int?)null }
            : new { MaxEventId = 1, NeverCommittedCount = 0, FirstNeverCommittedId = (int?)null, LastNeverCommittedId = (int?)null, WaitingForEventId = (int?)2 });
        checkpoint.FailedEventIds.Should().Equal([4], "the events above a missing id are checked either way");
        var details = audit.Single().DetailsJson;
        if (passed)
        {
            details.Should().Contain("\"neverCommittedCount\":2").And.Contain("\"firstNeverCommittedId\":2").And.Contain("\"lastNeverCommittedId\":3");
            logs.Entries.Should().ContainSingle(x => x.EventId.Id == SecurityAuditLogEvents.Checkpoint.Id)
                .Which.Message.Should().Contain("NeverCommitted=2").And.Contain("FirstNeverCommittedId=2").And.Contain("LastNeverCommittedId=3");
        }
        else
        {
            details.Should().Contain("\"maxEventId\":1").And.Contain("\"waitingForEventId\":2");
        }
    }

    [TestCase(false, "FROM beacon.security_audit_events", "s.id > @", "ORDER BY s.id", "LIMIT ")]
    [TestCase(true, "FROM [beacon].[SecurityAuditEvents]", "[s].[Id] > @", "ORDER BY [s].[Id]", "TOP(")]
    public async Task TheScheduledVerification_ScansById_NeverByTime_Translates(bool sqlServer, params string[] expected)
    {
        var capture = (sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture()).ThenNoRows().ThenNoRows();

        await new SecurityAuditVerificationService(
                capture.Factory(),
                SecurityAuditTestKit.Integrity,
                new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest),
                new SecurityAuditReadGate(TimeProvider.System),
                Options.Create(new SecurityAuditOptions()),
                new FakeTimeProvider(Now),
                NullLoggerFactory.Instance,
                Mock.Of<IMcpAuditVerificationService>())
            .VerifyNewEventsAsync(CancellationToken.None);

        capture.Commands.Should().HaveCount(2, "the checkpoints, then the first batch of the scan");
        var scan = capture.Commands[1];
        foreach (var fragment in expected)
        {
            scan.Should().Contain(fragment);
        }

        scan[scan.IndexOf("WHERE", StringComparison.Ordinal)..].Should().NotContainAny(["created_time", "CreatedTime"], "an event is never selected by the time it carries");
        capture.CommandParameters[1].Values.OfType<DateTime>().Should().BeEmpty();
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

    // The rows, with a writer that acts once, right after the purge's third read has returned: the first read of its
    // deleting pass, after the verifying pass read the expired rows and found no more. Each read, and each bulk delete's
    // match, reads every row once.
    private static IDbContextFactory<BeaconContext> FactoryWithWriter(List<SecurityAuditEvent> rows, Action write)
    {
        var saved = new List<object>();
        var reads = new WrittenAfterRead(rows, 3, write);
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(new Dictionary<Type, object> { [typeof(SecurityAuditEvent)] = RecordingBeaconContext.MemorySet(rows, saved, reads) }, saved));

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
            LoggerFactory.Create(x => x.AddProvider(logs)),
            Mock.Of<IMcpAuditVerificationService>());
    }

    // The stored rows as they are when read; once the read numbered afterRead has gone through every row, the writer acts.
    private sealed class WrittenAfterRead(List<SecurityAuditEvent> rows, int afterRead, Action write) : IEnumerable<SecurityAuditEvent>
    {
        private int _reads;

        public IEnumerator<SecurityAuditEvent> GetEnumerator()
        {
            var stored = rows.ToList();
            foreach (var row in stored)
            {
                yield return row;
            }

            if (++_reads == afterRead)
            {
                write();
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
