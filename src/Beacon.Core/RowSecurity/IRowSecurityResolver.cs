using Beacon.Core.Services.Validation;

namespace Beacon.Core.RowSecurity;

/// <summary>
/// Resolves row-level security for the scope's caller on one data source, right before a statement on it passes
/// <see cref="ISqlExecutionGate"/>: every path that runs SQL on a source, on a request or off it (scheduled work, sampling,
/// discovery, eval), takes its <see cref="RowSecurityContext"/> from here and never states one itself. The caller is the
/// request's caller, or, in a scope that runs as the owner of scheduled work, that owner. An embed render's caller is its
/// grant, acting for the grant's creator: the creator's entitlement applies, resolved live on every render. Registered
/// scoped.
/// </summary>
public interface IRowSecurityResolver
{
    /// <param name="dataSourceId">The data source the statement runs on.</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    Task<RowSecurityContext> ResolveAsync(int dataSourceId, CancellationToken cancellationToken);
}
