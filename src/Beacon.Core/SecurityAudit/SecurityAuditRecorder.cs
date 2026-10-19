using System.Diagnostics;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Helpers;
using Beacon.Core.Mcp;
using Beacon.Core.Telemetry;

namespace Beacon.Core.SecurityAudit;

/// <summary>
/// The recorder <see cref="ISecurityAuditRecorder"/> describes. The request's caller becomes the event's actor as the
/// project access evaluator classifies it: a Beacon user (session, API key owner, provisioned MCP caller) by its
/// <c>Users.Id</c>, an API key also by its id, an MCP caller without a user by its subject hash, a session without a stored
/// user by a keyed hash of the id it presents, background work as the system. Never a name, an e-mail address or an
/// external id. An entry that names its actor never resolves the request's caller, so a request that has not authenticated
/// yet (a sign-in, first-run setup, a caller provisioned on its first request) does not fix its caller as anonymous for the
/// rest of the scope. While a change holds its locks nothing is read on another connection: a settled caller is resolved
/// before the change runs, and the people an event names are read through the change's own context.
/// </summary>
internal sealed class SecurityAuditRecorder(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconActorAccessor actorAccessor,
    SecurityAuditIntegrity integrity,
    McpCallerSubjectHasher callerHasher,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory,
    IBeaconRequestPrincipal requestPrincipal) : ISecurityAuditRecorder
{
    /// <summary>The tenant a host session's presented id is hashed under, apart from any identity provider's tenants.</summary>
    internal const string SessionTenant = "beacon-session";

    /// <summary>How long a write on its own may take before it is given up (and logged as not written): a database that hangs never hangs a sign-in.</summary>
    internal static readonly TimeSpan StandaloneWriteTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The actor kind the log stream names when an event that could not be written had no actor resolved yet.</summary>
    internal const string UnresolvedActorKind = "unresolved";

    internal const string NotReplayableMessage =
        "The change was saved by an attempt whose transaction did not commit, and a retried attempt cannot save it again: nothing was committed. Retry the request.";

    private readonly ILogger _auditLogger = loggerFactory.CreateLogger(BeaconTelemetry.AuditLogCategory);

    public async Task StageAsync(BeaconContext context, SecurityAuditEntry entry, CancellationToken cancellationToken)
    {
        var caller = entry.Actor ?? await CallerAsync(cancellationToken);
        var named = await NamedUserIdsAsync(context, [entry], cancellationToken);
        context.SecurityAuditEvents.Add(Build(entry, caller, named));
    }

    public Task SaveWithEventsAsync(BeaconContext context, Func<IEnumerable<SecurityAuditEntry>> events, CancellationToken cancellationToken)
    {
        return SaveAsync(context, () => context.SaveChangesAsync(cancellationToken), _ => events(), replayable: false, cancellationToken);
    }

    public Task<T> SaveWithEventsAsync<T>(
        BeaconContext context,
        Func<Task<T>> change,
        Func<T, IEnumerable<SecurityAuditEntry>> events,
        CancellationToken cancellationToken)
    {
        return SaveAsync(context, change, events, replayable: true, cancellationToken);
    }

    public async Task RecordAsync(SecurityAuditEntry entry, CancellationToken cancellationToken)
    {
        // What the log stream gets when the write fails: the event as far as it was built.
        SecurityAuditEvent? lost = null;
        try
        {
            // Not cancelled with the request (a caller that goes away mid-attempt is still on record), but bounded: a
            // database that does not answer is given up on, and the event goes to the log stream instead.
            using var timeout = new CancellationTokenSource(StandaloneWriteTimeout, timeProvider);
            var caller = entry.Actor ?? await CallerAsync(timeout.Token);
            lost = Unsealed(entry.WithDigests(integrity), caller);
            await using var context = await contextFactory.CreateDbContextAsync(timeout.Token);
            var auditEvent = Build(entry, caller, await NamedUserIdsAsync(context, [entry], timeout.Token));
            lost = auditEvent;
            context.SecurityAuditEvents.Add(auditEvent);
            await context.SaveChangesAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            // Fail open: the action the event records goes on; the event itself goes to the log stream (9107, Error,
            // Persisted=False) and is counted, so the gap is operationally visible and the stream still holds it.
            NotWritten(entry, lost, ex);
        }
    }

    public string CallerHashOf(string tenantOrIssuer, string subject)
    {
        return callerHasher.Hash(tenantOrIssuer, subject);
    }

    /// <summary>The stored form of <paramref name="entry"/> by <paramref name="actor"/> at <paramref name="now"/>, sealed.</summary>
    internal static SecurityAuditEvent ToEvent(
        SecurityAuditEntry entry,
        SecurityAuditActor actor,
        DateTime now,
        string? traceId,
        SecurityAuditIntegrity integrity)
    {
        var auditEvent = Unsealed(entry.WithDigests(integrity), actor);
        auditEvent.CreatedTime = SecurityAuditIntegrity.StoredTime(now);
        auditEvent.TraceId = traceId;
        integrity.Seal(auditEvent);

        return auditEvent;
    }

    /// <summary>The caller <paramref name="actor"/> as an event's actor (see the class).</summary>
    internal static SecurityAuditActor ActorOf(BeaconActor actor, ClaimsPrincipal? principal, McpCallerSubjectHasher callerHasher)
    {
        return actor.Kind switch
        {
            BeaconPrincipalKind.User => new SecurityAuditActor(SecurityAuditActorKinds.User, actor.BeaconUserId),
            BeaconPrincipalKind.ApiKey => new SecurityAuditActor(SecurityAuditActorKinds.ApiKey, actor.BeaconUserId, actor.ApiKeyId),
            BeaconPrincipalKind.McpCaller => new SecurityAuditActor(
                SecurityAuditActorKinds.McpCaller,
                actor.BeaconUserId,
                CallerHash: actor.BeaconUserId == null ? principal?.FindFirst(McpCallerClaimTypes.CallerHash)?.Value : null),
            BeaconPrincipalKind.Session => new SecurityAuditActor(
                SecurityAuditActorKinds.Session,
                CallerHash: actor.UserId == null ? null : callerHasher.Hash(SessionTenant, actor.UserId)),

            // Scheduled work run as its owner: the system, for the owner it acts for.
            BeaconPrincipalKind.Owner => new SecurityAuditActor(SecurityAuditActorKinds.System, actor.BeaconUserId),
            BeaconPrincipalKind.System => SecurityAuditActor.System,
            _ => SecurityAuditActor.Anonymous
        };
    }

    // The fields of the stored event an entry and its actor decide; time, trace and seal are added by ToEvent.
    private static SecurityAuditEvent Unsealed(SecurityAuditEntry entry, SecurityAuditActor actor)
    {
        return new SecurityAuditEvent
        {
            EventGuid = Guid.NewGuid(),
            Category = SecurityAuditActions.CategoryOf(entry.Action),
            Action = entry.Action,
            Outcome = entry.Outcome,
            ActorKind = actor.Kind,
            ActorUserId = actor.UserId,
            ApiKeyId = actor.ApiKeyId,
            CallerHash = actor.CallerHash,
            ProjectId = entry.ProjectId,
            ResourceType = entry.ResourceType,
            ResourceId = entry.ResourceId,
            DetailsJson = entry.Details?.ToJson(),
            KeyId = string.Empty,
            Hmac = string.Empty
        };
    }

    /// <summary>
    /// Runs the change and saves the events built from it in one transaction. Once the request's caller is settled (the
    /// request has authenticated, or the work has no request) it is resolved first, before any transaction holds a lock.
    /// While the request has not authenticated (a sign-in, first-run setup, a caller provisioned on its first request) it
    /// is resolved only for an event that names no actor: resolving it then would fix the scope's caller as anonymous
    /// before authentication sets it, and an unauthenticated request's caller is answered without reading anything. Inside
    /// the caller's transaction this runs once: the caller's execution strategy owns any retry. Otherwise it opens the
    /// transaction through the context's execution strategy, and a retried attempt starts over: a replayable change (a
    /// bulk update or delete, a delegate that reads what it changes) runs again on a cleared tracker, so no event of the
    /// failed attempt is saved twice; the tracked changes of the save overload are replayed only when the failed attempt
    /// never saved them, because a save that succeeded accepted them (their generated keys with them) and a retry would
    /// lose or duplicate them, so that retry fails with nothing committed.
    /// </summary>
    private async Task<T> SaveAsync<T>(
        BeaconContext context,
        Func<Task<T>> change,
        Func<T, IEnumerable<SecurityAuditEntry>> events,
        bool replayable,
        CancellationToken cancellationToken)
    {
        var resolved = CallerIsSettled() ? await CallerAsync(cancellationToken) : null;
        async Task<SecurityAuditActor> Caller() => resolved ??= await CallerAsync(cancellationToken);

        if (context.Database.CurrentTransaction != null)
        {
            return await ChangeThenEventsAsync(context, change, events, Caller, new Attempt(), cancellationToken);
        }

        var strategy = context.Database.CreateExecutionStrategy();
        Attempt? previous = null;

        return await strategy.ExecuteAsync(async () =>
        {
            if (previous != null)
            {
                StartOver(context, previous, replayable);
            }

            var attempt = new Attempt();
            previous = attempt;
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var result = await ChangeThenEventsAsync(context, change, events, Caller, attempt, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return result;
        });
    }

    // Before a retried attempt: nothing the failed one added to the tracker is saved again.
    private static void StartOver(BeaconContext context, Attempt failed, bool replayable)
    {
        if (replayable)
        {
            context.ChangeTracker.Clear();

            return;
        }

        foreach (var staged in failed.Staged)
        {
            context.Entry(staged).State = EntityState.Detached;
        }

        if (failed.ChangeSaved)
        {
            throw new InvalidOperationException(NotReplayableMessage);
        }
    }

    private async Task<T> ChangeThenEventsAsync<T>(
        BeaconContext context,
        Func<Task<T>> change,
        Func<T, IEnumerable<SecurityAuditEntry>> events,
        Func<Task<SecurityAuditActor>> caller,
        Attempt attempt,
        CancellationToken cancellationToken)
    {
        var result = await change();
        attempt.ChangeSaved = true;
        var entries = events(result).ToList();
        if (entries.Count == 0)
        {
            return result;
        }

        // Read through the change's own context: on its connection, inside its transaction.
        var named = await NamedUserIdsAsync(context, entries, cancellationToken);
        foreach (var entry in entries)
        {
            var auditEvent = Build(entry, entry.Actor ?? await caller(), named);
            context.SecurityAuditEvents.Add(auditEvent);
            attempt.Staged.Add(auditEvent);
        }

        await context.SaveChangesAsync(cancellationToken);

        return result;
    }

    private async Task<SecurityAuditActor> CallerAsync(CancellationToken cancellationToken)
    {
        return ActorOf(await actorAccessor.GetCurrentAsync(cancellationToken), requestPrincipal.User, callerHasher);
    }

    // Whether the request's caller can no longer change: no request (the scope decides), or a request that has authenticated.
    private bool CallerIsSettled()
    {
        return !requestPrincipal.HasRequest || requestPrincipal.User?.Identity?.IsAuthenticated == true;
    }

    private SecurityAuditEvent Build(SecurityAuditEntry entry, SecurityAuditActor actor, IReadOnlyDictionary<string, int> named)
    {
        return ToEvent(entry.WithNamedUserIds(named), actor, timeProvider.GetUtcNow().UtcDateTime, Activity.Current?.TraceId.ToHexString(), integrity);
    }

    // The Users.Id of each person the entries name (archived users too), read through the given context.
    private static async Task<IReadOnlyDictionary<string, int>> NamedUserIdsAsync(
        BeaconContext context,
        IReadOnlyCollection<SecurityAuditEntry> entries,
        CancellationToken cancellationToken)
    {
        var externalIds = SecurityAuditUserIds.NamedBy(entries);
        if (externalIds.Count == 0)
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

        return await SecurityAuditUserIds.OfAsync(context, externalIds.ToList(), cancellationToken);
    }

    private void NotWritten(SecurityAuditEntry entry, SecurityAuditEvent? lost, Exception ex)
    {
        BeaconTelemetry.SecurityAuditWriteFailures.Add(1, new KeyValuePair<string, object?>(BeaconTelemetry.SecurityAuditActionTag, entry.Action));
        try
        {
            // The event as far as it was built; when not even its actor was resolved, what the entry says.
            SecurityAuditLogEvents.LogNotPersisted(
                _auditLogger,
                lost ?? Unsealed(entry, entry.Actor ?? new SecurityAuditActor(UnresolvedActorKind)),
                FailureCodes.Describe(ex));
        }
        catch (Exception logFailure)
        {
            // RecordAsync never throws; the failure is already counted, and a logger that throws cannot report itself.
            Debug.WriteLine($"Security audit write failure could not be logged: {logFailure.GetType().Name}");
        }
    }

    // What one attempt of a save added to the tracker, and whether its change ran.
    private sealed class Attempt
    {
        public bool ChangeSaved { get; set; }

        public List<SecurityAuditEvent> Staged { get; } = [];
    }
}
