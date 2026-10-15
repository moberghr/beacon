using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Mcp;
using Beacon.Core.SecurityAudit;
using Moq;

namespace Beacon.Tests.Common;

/// <summary>
/// The shared kit for security audit tests: a test integrity key and caller hasher, the check that an event carries no
/// personal data or secret (<see cref="AssertNoPii"/>), and <see cref="RecordingSecurityAudit"/>, a recorder that keeps
/// the events it is given instead of writing them.
/// </summary>
internal static partial class SecurityAuditTestKit
{
    /// <summary>A throwaway integrity secret; never a real one (§1.2).</summary>
    public const string TestSecret = "security-audit-test-secret-0123456789abcdef";

    public static SecurityAuditIntegrity Integrity { get; } = new(TestSecret, []);

    public static McpCallerSubjectHasher CallerHasher { get; } = new(TestSecret);

    /// <summary>
    /// The recorded reads of audit data over <paramref name="factory"/>, written through <paramref name="audit"/>, for
    /// <paramref name="actor"/> (a user, by default), with a fresh read gate (no read recorded yet, no verification running).
    /// </summary>
    public static SecurityAuditReads Reads(IDbContextFactory<BeaconContext> factory, ISecurityAuditRecorder audit, BeaconActor? actor = null, SecurityAuditReadGate? gate = null)
    {
        return new SecurityAuditReads(
            factory,
            audit,
            RecordingSecurityAudit.Accessor(actor ?? RecordingSecurityAudit.User(9, "ext-auditor")),
            gate ?? new SecurityAuditReadGate(TimeProvider.System));
    }

    /// <summary>
    /// Fails when any field of <paramref name="auditEvent"/> contains one of <paramref name="forbidden"/> (case ignored) —
    /// the names, e-mail addresses, external ids, typed login names, tokens, SQL or destinations a test put into play — or
    /// anything shaped like an e-mail address, an IP address or SQL.
    /// </summary>
    public static void AssertNoPii(SecurityAuditEvent auditEvent, params string?[] forbidden)
    {
        var fields = Fields(auditEvent);
        foreach (var value in forbidden.Where(x => !string.IsNullOrEmpty(x)))
        {
            fields.Should().NotContain(x => x.Contains(value!, StringComparison.OrdinalIgnoreCase), $"the event must not carry '{value}'");
        }

        fields.Should().NotContain(x => EmailShape().IsMatch(x), "an event never carries an e-mail address");
        fields.Should().NotContain(x => IpShape().IsMatch(x), "an event never carries an IP address");
        fields.Should().NotContain(x => SqlShape().IsMatch(x), "an event never carries SQL");
    }

    private static List<string> Fields(SecurityAuditEvent auditEvent)
    {
        return new[]
            {
                auditEvent.Category,
                auditEvent.Action,
                auditEvent.Outcome,
                auditEvent.ActorKind,
                auditEvent.CallerHash,
                auditEvent.ResourceType,
                auditEvent.ResourceId,
                auditEvent.DetailsJson,
                auditEvent.TraceId
            }
            .OfType<string>()
            .ToList();
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailShape();

    [GeneratedRegex(@"(?<![\d.])\d{1,3}(\.\d{1,3}){3}(?![\d.])")]
    private static partial Regex IpShape();

    [GeneratedRegex(@"\b(select|insert|update|delete)\s", RegexOptions.IgnoreCase)]
    private static partial Regex SqlShape();
}

/// <summary>
/// A recorder that keeps every event it is given, built as the real recorder builds them (actor from
/// <paramref name="caller"/>, details serialised, sealed with the test key), and writes nothing: <c>StageAsync</c> does
/// not add to the context, <c>SaveWithEventsAsync</c> runs the change and keeps the events built from its result. A staged
/// event joins its context's next save, so <see cref="Single"/> and <see cref="Of"/> (and
/// <see cref="AssertStagedEventsSaved"/>) fail when a context an event was staged on was not saved after the staging:
/// staging after the save, or on another context than the one saved, writes nothing.
/// </summary>
internal sealed class RecordingSecurityAudit : ISecurityAuditRecorder
{
    private readonly BeaconActor? _caller;
    private readonly List<StagedEvent> _staged = [];

    public RecordingSecurityAudit(BeaconActor? caller = null)
    {
        _caller = caller;
        ResolveUser = x => UserIds.TryGetValue(x, out var id) ? id : null;
    }

    public List<SecurityAuditEvent> Events { get; } = [];

    /// <summary>How each event in <see cref="Events"/> was handed over: <c>staged</c>, <c>saved</c> or <c>recorded</c>.</summary>
    public List<string> Ways { get; } = [];

    /// <summary>
    /// The <c>Users.Id</c> of a person an entry names by external id (<see cref="SecurityAuditEntry.Naming"/>), as the
    /// recorder reads it from the user store; by default from <see cref="UserIds"/>.
    /// </summary>
    public Func<string, int?> ResolveUser { get; set; }

    /// <summary>The users <see cref="ResolveUser"/> knows by default, by external id.</summary>
    public Dictionary<string, int> UserIds { get; } = new(StringComparer.Ordinal);

    public Task StageAsync(BeaconContext context, SecurityAuditEntry entry, CancellationToken cancellationToken)
    {
        Staged(context, Keep(entry, "staged"));

        return Task.CompletedTask;
    }

    public Task SaveWithEventsAsync(BeaconContext context, Func<IEnumerable<SecurityAuditEntry>> events, CancellationToken cancellationToken)
    {
        return SaveWithEventsAsync(context, () => context.SaveChangesAsync(cancellationToken), _ => events(), cancellationToken);
    }

    /// <summary>
    /// Fails when an event was staged on a context that was not saved after it: the real recorder adds a staged event to
    /// the context, and only the context's next save writes it.
    /// </summary>
    public void AssertStagedEventsSaved()
    {
        List<string> lost;
        lock (Events)
        {
            lost = _staged
                .Where(x => !x.Saved)
                .Select(x => x.Event.Action)
                .ToList();
        }

        lost.Should().BeEmpty(
            "a staged event is written by its context's next save; one staged after the save, or on a context never saved, is lost ({0})",
            string.Join(", ", lost));
    }

    public async Task<T> SaveWithEventsAsync<T>(
        BeaconContext context,
        Func<Task<T>> change,
        Func<T, IEnumerable<SecurityAuditEntry>> events,
        CancellationToken cancellationToken)
    {
        var result = await change();
        foreach (var entry in events(result))
        {
            Keep(entry, "saved");
        }

        return result;
    }

    public Task RecordAsync(SecurityAuditEntry entry, CancellationToken cancellationToken)
    {
        Keep(entry, "recorded");

        return Task.CompletedTask;
    }

    public string CallerHashOf(string tenantOrIssuer, string subject)
    {
        return SecurityAuditTestKit.CallerHasher.Hash(tenantOrIssuer, subject);
    }

    /// <summary>The events with <paramref name="action"/> (after <see cref="AssertStagedEventsSaved"/>).</summary>
    public List<SecurityAuditEvent> Of(string action)
    {
        AssertStagedEventsSaved();

        return Events
            .Where(x => x.Action == action)
            .ToList();
    }

    /// <summary>
    /// The one event recorded, after checking there is exactly one, that it carries none of <paramref name="forbidden"/>
    /// (<see cref="SecurityAuditTestKit.AssertNoPii"/>) and, when it was staged, that its context was saved after it.
    /// </summary>
    public SecurityAuditEvent Single(params string?[] forbidden)
    {
        AssertStagedEventsSaved();
        var auditEvent = Events.Should().ContainSingle().Subject;
        SecurityAuditTestKit.AssertNoPii(auditEvent, forbidden);

        return auditEvent;
    }

    /// <summary>
    /// A recorder that keeps its events here, with the actor each request's <paramref name="actors"/> resolves (for code
    /// resolved from a container, where the caller is the request's).
    /// </summary>
    public ISecurityAuditRecorder For(IBeaconActorAccessor actors)
    {
        return new ResolvingRecorder(this, actors);
    }

    /// <summary>A user as an event's caller: <c>Users.Id</c> <paramref name="userId"/>, recorded as <paramref name="externalId"/>.</summary>
    public static BeaconActor User(int userId, string externalId, bool isAdmin = false)
    {
        return new BeaconActor(externalId, isAdmin) { Kind = BeaconPrincipalKind.User, BeaconUserId = userId, IsActiveUser = true };
    }

    /// <summary>An actor accessor that resolves every request to <paramref name="actor"/>.</summary>
    public static IBeaconActorAccessor Accessor(BeaconActor actor)
    {
        var accessor = new Mock<IBeaconActorAccessor>();
        accessor
            .Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(actor);

        return accessor.Object;
    }

    private SecurityAuditEvent Keep(SecurityAuditEntry entry, string way, BeaconActor? requestCaller = null)
    {
        var actor = entry.Actor ?? SecurityAuditRecorder.ActorOf(requestCaller ?? _caller ?? BeaconActor.Nobody, null, SecurityAuditTestKit.CallerHasher);
        var named = SecurityAuditUserIds.NamedBy([entry])
            .Select(x => (ExternalId: x, Id: ResolveUser(x)))
            .Where(x => x.Id != null)
            .ToDictionary(x => x.ExternalId, x => x.Id!.Value, StringComparer.Ordinal);
        var auditEvent = SecurityAuditRecorder.ToEvent(entry.WithNamedUserIds(named), actor, DateTime.UtcNow, null, SecurityAuditTestKit.Integrity);
        lock (Events)
        {
            Events.Add(auditEvent);
            Ways.Add(way);
        }

        return auditEvent;
    }

    // Notes that the context's next save (an attempt is enough: a save that fails takes the event down with it) writes the
    // event: a test context that does not reach EF's SaveChanges tells its saves itself (IObservableSaves), any other
    // context through EF's SavingChanges.
    private void Staged(BeaconContext context, SecurityAuditEvent auditEvent)
    {
        var staged = new StagedEvent(auditEvent);
        lock (Events)
        {
            _staged.Add(staged);
        }

        if (context is IObservableSaves observable)
        {
            observable.Saving += () => staged.Saved = true;

            return;
        }

        context.SavingChanges += (_, _) => staged.Saved = true;
    }

    private sealed class StagedEvent(SecurityAuditEvent auditEvent)
    {
        public SecurityAuditEvent Event { get; } = auditEvent;

        public bool Saved { get; set; }
    }

    private sealed class ResolvingRecorder(RecordingSecurityAudit audit, IBeaconActorAccessor actors) : ISecurityAuditRecorder
    {
        public async Task StageAsync(BeaconContext context, SecurityAuditEntry entry, CancellationToken cancellationToken)
        {
            audit.Staged(context, audit.Keep(entry, "staged", await actors.GetCurrentAsync(cancellationToken)));
        }

        public Task SaveWithEventsAsync(BeaconContext context, Func<IEnumerable<SecurityAuditEntry>> events, CancellationToken cancellationToken)
        {
            return SaveWithEventsAsync(context, () => context.SaveChangesAsync(cancellationToken), _ => events(), cancellationToken);
        }

        public async Task<T> SaveWithEventsAsync<T>(
            BeaconContext context,
            Func<Task<T>> change,
            Func<T, IEnumerable<SecurityAuditEntry>> events,
            CancellationToken cancellationToken)
        {
            var result = await change();
            var caller = await actors.GetCurrentAsync(cancellationToken);
            foreach (var entry in events(result))
            {
                audit.Keep(entry, "saved", caller);
            }

            return result;
        }

        public async Task RecordAsync(SecurityAuditEntry entry, CancellationToken cancellationToken)
        {
            audit.Keep(entry, "recorded", await actors.GetCurrentAsync(cancellationToken));
        }

        public string CallerHashOf(string tenantOrIssuer, string subject)
        {
            return audit.CallerHashOf(tenantOrIssuer, subject);
        }
    }
}
