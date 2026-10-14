using System.Globalization;

namespace Beacon.Core.SecurityAudit;

/// <summary>
/// An event an event site hands to <see cref="ISecurityAuditRecorder"/>: the action (from
/// <see cref="SecurityAuditActions"/>), how it ended, the resource and project it concerns and its details. The actor is
/// the request's caller unless <see cref="Actor"/> names another (a sign-in names the user it signed in). Build it with
/// <c>new SecurityAuditEntry(action).On(resource, id).InProject(projectId).With(details)</c>.
/// </summary>
public sealed record SecurityAuditEntry
{
    public SecurityAuditEntry(string action)
    {
        if (!SecurityAuditActions.All.Contains(action))
        {
            throw new ArgumentException("A security audit action must be one of SecurityAuditActions.", nameof(action));
        }

        Action = action;
    }

    public string Action { get; }

    public string Outcome { get; init; } = SecurityAuditOutcomes.Success;

    public string? ResourceType { get; init; }

    public string? ResourceId { get; init; }

    public int? ProjectId { get; init; }

    public SecurityAuditDetails? Details { get; init; }

    /// <summary>Who acted, when it is not the request's caller; null for the caller.</summary>
    public SecurityAuditActor? Actor { get; init; }

    /// <summary>
    /// People the event names by the <c>Users.ExternalId</c> a resource recorded for them (an owner, a requester, an
    /// assignee): the recorder writes each one's <c>Users.Id</c> into the details under its key (null for nobody or an
    /// unknown id), never the external id itself.
    /// </summary>
    public IReadOnlyList<SecurityAuditNamedUser> NamedUsers { get; init; } = [];

    // Values the event shows only as keyed digests (see Digesting); internal, so no printout of the entry shows them.
    internal IReadOnlyList<(string Key, IReadOnlyList<string?> Values)> DigestedValues { get; init; } = [];

    public SecurityAuditEntry On(string resourceType, int? resourceId)
    {
        return this with { ResourceType = resourceType, ResourceId = resourceId?.ToString(CultureInfo.InvariantCulture) };
    }

    public SecurityAuditEntry InProject(int? projectId)
    {
        return this with { ProjectId = projectId };
    }

    public SecurityAuditEntry With(SecurityAuditDetails details)
    {
        return this with { Details = details };
    }

    public SecurityAuditEntry By(SecurityAuditActor actor)
    {
        return this with { Actor = actor };
    }

    /// <summary>Names the user recorded as <paramref name="externalId"/> under <paramref name="key"/>, by <c>Users.Id</c> (see <see cref="NamedUsers"/>).</summary>
    public SecurityAuditEntry Naming(string key, string? externalId)
    {
        return this with { NamedUsers = [.. NamedUsers, new SecurityAuditNamedUser(key, externalId)] };
    }

    /// <summary>
    /// Shows <paramref name="values"/> under <paramref name="key"/> as keyed digests only
    /// (<see cref="SecurityAuditIntegrity.Digest"/>), in order: an event can show that a value changed, and to what digest,
    /// without holding it. The values never reach the details, the log stream or a printout of the entry.
    /// </summary>
    internal SecurityAuditEntry Digesting(string key, IEnumerable<string?> values)
    {
        return this with { DigestedValues = [.. DigestedValues, (key, values.ToList())] };
    }

    /// <summary>This entry with its digested values written into its details as digests under <paramref name="integrity"/>'s key.</summary>
    internal SecurityAuditEntry WithDigests(SecurityAuditIntegrity integrity)
    {
        if (DigestedValues.Count == 0)
        {
            return this;
        }

        var details = Details ?? new SecurityAuditDetails();
        foreach (var (key, values) in DigestedValues)
        {
            details.AddDigests(key, values.Select(integrity.Digest));
        }

        return this with { Details = details, DigestedValues = [] };
    }

    /// <summary>
    /// This entry with each named user written into its details as the <c>Users.Id</c> <paramref name="userIds"/> holds for
    /// their external id (null when it holds none).
    /// </summary>
    internal SecurityAuditEntry WithNamedUserIds(IReadOnlyDictionary<string, int> userIds)
    {
        if (NamedUsers.Count == 0)
        {
            return this;
        }

        var details = Details ?? new SecurityAuditDetails();
        foreach (var named in NamedUsers)
        {
            details.Add(named.Key, named.ExternalId != null && userIds.TryGetValue(named.ExternalId, out var id) ? id : null);
        }

        return this with { Details = details, NamedUsers = [] };
    }

    public SecurityAuditEntry Ending(string outcome)
    {
        return this with { Outcome = outcome };
    }
}

/// <summary>A person an event names, by the external id a resource recorded; written as their <c>Users.Id</c> under <see cref="Key"/>.</summary>
public sealed record SecurityAuditNamedUser(string Key, string? ExternalId);

/// <summary>
/// The actor of an event when it is not the request's caller: its kind (<see cref="SecurityAuditActorKinds"/>), its
/// <c>Users.Id</c>, the API key it used, or a keyed hash naming a caller without a Beacon user.
/// </summary>
public sealed record SecurityAuditActor(string Kind, int? UserId = null, int? ApiKeyId = null, string? CallerHash = null)
{
    /// <summary>No authenticated caller.</summary>
    public static SecurityAuditActor Anonymous { get; } = new(SecurityAuditActorKinds.Anonymous);

    /// <summary>Work Beacon does for itself.</summary>
    public static SecurityAuditActor System { get; } = new(SecurityAuditActorKinds.System);

    /// <summary>A Beacon user (signed in, or signing in).</summary>
    public static SecurityAuditActor OfUser(int userId)
    {
        return new SecurityAuditActor(SecurityAuditActorKinds.User, userId);
    }

    /// <summary>A caller without a Beacon user, named by a keyed hash (<see cref="ISecurityAuditRecorder.CallerHashOf"/>).</summary>
    public static SecurityAuditActor OfCaller(string callerHash)
    {
        return new SecurityAuditActor(SecurityAuditActorKinds.Anonymous, CallerHash: callerHash);
    }
}
