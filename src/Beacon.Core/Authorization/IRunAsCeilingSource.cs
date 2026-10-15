namespace Beacon.Core.Authorization;

/// <summary>
/// The global role that caps an actor who is not the current request's signed-in caller: the owner of scheduled work,
/// or a mapped MCP user caller. The default reads the user's role levels from the user store; a host with its own
/// authorization provider can supply its own with <c>TryAdd</c>.
/// </summary>
public interface IRunAsCeilingSource
{
    /// <summary>
    /// <see cref="ProjectRole.Viewer"/> for a reader, <see cref="ProjectRole.Editor"/> for a writer, null for neither.
    /// </summary>
    Task<ProjectRole?> GetCeilingAsync(BeaconActor owner, CancellationToken cancellationToken);
}
