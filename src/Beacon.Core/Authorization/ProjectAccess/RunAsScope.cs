using Microsoft.AspNetCore.Http;

namespace Beacon.Core.Authorization.ProjectAccess;

/// <summary>
/// The default <see cref="IRunAsScope"/>: the owner per DI scope, set once. The actor accessor reports when it resolves
/// the scope's caller (<see cref="MarkCallerResolved"/>), and the evaluator memoizes its decision per scope, so setting
/// the owner after that would leave the scope deciding for whoever was resolved first: it throws instead.
/// </summary>
internal sealed class RunAsScope(IHttpContextAccessor httpContextAccessor) : IRunAsScope
{
    private bool _callerResolved;

    public bool IsSet { get; private set; }

    public string? OwnerUserId { get; private set; }

    public void RunAs(string? ownerUserId)
    {
        if (IsSet)
        {
            throw new InvalidOperationException("This scope already runs as the owner of scheduled work; the owner is set once.");
        }

        if (httpContextAccessor.HttpContext != null)
        {
            throw new InvalidOperationException("A request's scope acts as the request's caller; it never runs as the owner of scheduled work.");
        }

        if (_callerResolved)
        {
            throw new InvalidOperationException("The scope's caller was already resolved; set the owner before anything in the scope resolves its caller.");
        }

        OwnerUserId = ownerUserId;
        IsSet = true;
    }

    /// <summary>Called by the actor accessor when it resolves the caller of a scope without a request.</summary>
    internal void MarkCallerResolved()
    {
        _callerResolved = true;
    }
}
