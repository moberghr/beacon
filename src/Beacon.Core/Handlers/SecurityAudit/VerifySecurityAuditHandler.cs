using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Telemetry;

namespace Beacon.Core.Handlers.SecurityAudit;

/// <summary>
/// Checks every security audit event of a window (at most <see cref="GetSecurityAuditEventsHandler.MaxRangeDays"/> days),
/// or only the given events of it (at most <see cref="MaxEventIds"/>: the rows a page shows, or one row), against its HMAC:
/// an event whose fields were changed, or that was written without the integrity key, fails; so does an event under a key
/// this deployment does not hold (a key removed from the configuration), and one dated outside the range a genuine event
/// can have (<see cref="SecurityAuditVerification.PlausibleRange"/>), which is counted as out of range and not checked.
/// Asked for by id, every requested id gets its own verdict (<see cref="SecurityAuditVerdicts"/>), so nothing is taken for
/// intact without being checked. Events are read in batches. A deletion leaves nothing to check and is not found here. One
/// verification runs at a time in a process (another answers 429). The verification is recorded, with what it found,
/// before its result is returned, and fails when the event cannot be written
/// (<see cref="SecurityAuditActions.AuditVerified"/>; <see cref="SecurityAuditLogEvents.Read"/> on the log stream).
/// </summary>
internal sealed class VerifySecurityAuditHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    SecurityAuditIntegrity integrity,
    SecurityAuditReads reads,
    SecurityAuditReadGate gate,
    IOptions<SecurityAuditOptions> options,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory)
    : IRequestHandler<VerifySecurityAuditCommand, VerifySecurityAuditResult>
{
    internal const int BatchSize = 1000;
    internal const int MaxReportedEvents = 100;
    internal const int MaxEventIds = ListRequest.MaxPageSize;

    internal const string BusyMessage = "A verification of the security audit trail is already running; try again when it has finished.";

    private readonly ILogger _auditLogger = loggerFactory.CreateLogger(BeaconTelemetry.AuditLogCategory);

    public async Task<VerifySecurityAuditResult> Handle(VerifySecurityAuditCommand request, CancellationToken cancellationToken)
    {
        var (from, to) = SecurityAuditWindow.Validate(request.From, request.To, GetSecurityAuditEventsHandler.MaxRangeDays);
        var eventIds = request.EventIds is { Count: > 0 } ? request.EventIds.Distinct().ToList() : null;
        if (eventIds?.Count > MaxEventIds)
        {
            throw new InvalidOperationException($"At most {MaxEventIds} security audit events can be verified by id at once.");
        }

        if (!gate.TryStartVerification())
        {
            throw new BeaconRateLimitedException(BusyMessage);
        }

        try
        {
            var result = await VerifyAsync(from, to, eventIds, cancellationToken);

            _auditLogger.Log(
                LogLevel.Information,
                SecurityAuditLogEvents.Read,
                "Security audit verified: From={From} To={To} RequestedEvents={RequestedEvents} Checked={Checked} Modified={Modified} UnknownKey={UnknownKey} OutOfRange={OutOfRange} Outcome={Outcome}",
                from,
                to,
                eventIds?.Count,
                result.CheckedCount,
                result.ModifiedCount,
                result.UnknownKeyCount,
                result.OutOfRangeCount,
                result.Outcome);

            await reads.RecordAsync(
                new SecurityAuditEntry(SecurityAuditActions.AuditVerified)
                    .On(SecurityAuditResources.SecurityAudit, null)
                    .Ending(result.Outcome)
                    .With(new SecurityAuditDetails()
                        .Add("from", from)
                        .Add("to", to)
                        .Add("requestedEventCount", eventIds?.Count)
                        .Add("checkedCount", result.CheckedCount)
                        .Add("modifiedCount", result.ModifiedCount)
                        .Add("unknownKeyCount", result.UnknownKeyCount)
                        .Add("outOfRangeCount", result.OutOfRangeCount)
                        .Add("notFoundCount", result.Verdicts?.Count(x => x.Verdict == SecurityAuditVerdicts.NotFound))
                        .Add("failedEventIds", result.FailedEventIds)),
                cancellationToken);

            return result;
        }
        finally
        {
            gate.EndVerification();
        }
    }

    private async Task<VerifySecurityAuditResult> VerifyAsync(DateTime from, DateTime to, List<int>? eventIds, CancellationToken cancellationToken)
    {
        var range = SecurityAuditVerification.PlausibleRange(timeProvider.GetUtcNow(), options.Value.RetentionDays);
        var verdicts = new Dictionary<int, string>();
        var checkedCount = 0;
        var outOfRangeCount = 0;
        var failed = new List<int>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastId = 0;

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        while (true)
        {
            var batch = await context.SecurityAuditEvents
                .AsNoTracking()
                .Where(x => x.CreatedTime >= from)
                .Where(x => x.CreatedTime <= to)
                .Where(x => x.Id > lastId)
                .WhereIf(eventIds != null, x => eventIds!.Contains(x.Id))
                .OrderBy(x => x.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var auditEvent in batch)
            {
                var verdict = SecurityAuditVerification.VerdictOf(auditEvent, range, integrity);
                if (verdict == SecurityAuditVerdicts.OutOfRange)
                {
                    outOfRangeCount++;
                    failed.Add(auditEvent.Id);
                    verdicts[auditEvent.Id] = SecurityAuditVerdicts.NotFound;

                    continue;
                }

                checkedCount++;
                counts[verdict] = counts.GetValueOrDefault(verdict) + 1;
                verdicts[auditEvent.Id] = verdict;
                if (verdict != SecurityAuditVerdicts.Intact)
                {
                    failed.Add(auditEvent.Id);
                }
            }

            lastId = batch[^1].Id;
        }

        var modifiedCount = counts.GetValueOrDefault(SecurityAuditVerdicts.Modified);
        var unknownKeyCount = counts.GetValueOrDefault(SecurityAuditVerdicts.UnknownKey);
        var outcome = modifiedCount + unknownKeyCount + outOfRangeCount > 0
            ? SecurityAuditOutcomes.Failure
            : checkedCount == 0
                ? SecurityAuditOutcomes.Empty
                : SecurityAuditOutcomes.Success;

        return new VerifySecurityAuditResult(
            checkedCount,
            counts.GetValueOrDefault(SecurityAuditVerdicts.Intact),
            modifiedCount,
            unknownKeyCount,
            failed
                .Order()
                .Take(MaxReportedEvents)
                .ToList())
        {
            OutOfRangeCount = outOfRangeCount,
            Outcome = outcome,
            Verdicts = eventIds?
                .Select(x => new SecurityAuditEventVerdict(x, verdicts.GetValueOrDefault(x, SecurityAuditVerdicts.NotFound)))
                .ToList()
        };
    }
}

/// <summary>
/// Verifies the security audit events created from <c>From</c> to <c>To</c> (UTC, at most 93 days apart) against their
/// integrity hashes; with <c>EventIds</c> (at most 200), only those events of the window, each with its own verdict. For
/// an Auditor; one at a time (429 while another runs); the verification is recorded.
/// </summary>
[ProjectScopeExempt("the security audit trail, gated by the Auditor permission; not filtered by membership", Policy = BeaconPermissions.AuditorPolicy)]
public record VerifySecurityAuditCommand(DateTime? From, DateTime? To, IReadOnlyList<int>? EventIds = null) : IRequest<VerifySecurityAuditResult>;

/// <summary>
/// How many events were checked, how many verified, how many failed (changed or forged; under a key this deployment does
/// not hold), how many are dated outside the range a genuine event can have (not checked), and the ids of the first
/// failing events. <see cref="Outcome"/> is <c>success</c> (every checked event intact), <c>failure</c> (any event failed
/// or out of range) or <c>empty</c> (nothing to check: not a success). <see cref="Verdicts"/> answers a verification by
/// id: one verdict per requested id (<c>intact</c>, <c>modified</c>, <c>unknown_key</c>, <c>not_found</c>); null for a
/// window. Only an <c>intact</c> verdict says an event verified.
/// </summary>
public record VerifySecurityAuditResult(
    int CheckedCount,
    int IntactCount,
    int ModifiedCount,
    int UnknownKeyCount,
    List<int> FailedEventIds)
{
    public int OutOfRangeCount { get; init; }

    public string Outcome { get; init; } = SecurityAuditOutcomes.Success;

    public List<SecurityAuditEventVerdict>? Verdicts { get; init; }
}

/// <summary>The verdict on one requested event (<see cref="SecurityAuditVerdicts"/>).</summary>
public record SecurityAuditEventVerdict(int EventId, string Verdict);
