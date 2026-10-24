namespace Beacon.Core.BackgroundTasks;

/// <summary>
/// Where the functional permission that admitted a task's start came from, as recorded on the task
/// (<see cref="Data.Entities.BackgroundTask.PermissionSource"/>).
/// </summary>
public static class BackgroundTaskPermissionSources
{
    /// <summary>The requester is an Admin, or one of its stored functional roles grants the permission: re-checked live.</summary>
    public const string StoredRole = "role";

    /// <summary>
    /// Only the identity provider's app role mapped at the requester's sign-in granted it: honoured at run time only shortly
    /// after the start, while the stored user is still enabled and its security stamp unchanged.
    /// </summary>
    public const string IdentityProvider = "idp";
}

/// <summary>
/// What admitted the start of a task gated by a functional permission, recorded on the task: the source
/// (<see cref="BackgroundTaskPermissionSources"/>) and, for an identity-provider grant, a hash of the requester's security
/// stamp at the start (never the stamp).
/// </summary>
public sealed record BackgroundTaskGrant(string Source, string? RequesterStampHash = null);
