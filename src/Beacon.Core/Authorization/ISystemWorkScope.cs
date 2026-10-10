namespace Beacon.Core.Authorization;

/// <summary>
/// Marks the current DI scope as work Beacon does for itself, with no request behind it: a background job or a startup
/// task. The project access evaluator treats work without a request as the system. A job host marks each job's scope
/// before the job runs, and Beacon marks the startup scopes it creates; work without a request in a scope nobody marked
/// is still treated as the system, but logs a warning.
/// </summary>
public interface ISystemWorkScope
{
    /// <summary>True once <see cref="MarkAsSystemWork"/> was called in this scope.</summary>
    bool IsSystemWork { get; }

    /// <summary>Marks this scope as work Beacon does for itself. Has no effect on a request's own caller.</summary>
    void MarkAsSystemWork();
}
