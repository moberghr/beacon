namespace Beacon.Core.Authorization;

/// <summary>
/// Exempts a request (or one id property of it) from project authorization, with the reason. A request is exempt when
/// its endpoint is gated by a policy that already decides it (<see cref="Policy"/>, such as the Admin policy), when it
/// touches no project data, or when it is scoped to its caller. A property is exempt when its id is not a project
/// resource (a user id) or only narrows a scope-filtered list.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, Inherited = false)]
public sealed class ProjectScopeExemptAttribute(string reason) : Attribute
{
    /// <summary>The Beacon API's Admin endpoint policy.</summary>
    public const string AdminPolicy = "BeaconApiAdmin";

    public string Reason { get; } = reason;

    /// <summary>The endpoint policy that gates the exempt request, when one does; tests assert its endpoint carries it.</summary>
    public string? Policy { get; init; }
}
