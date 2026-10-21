using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;

namespace Beacon.Core.RowSecurity;

/// <summary>
/// The resolver while row-level security cannot be enforced: with the lock <c>Beacon:RowSecurity:Enabled</c> off (the
/// default) no row filter applies to any source. The lock is refused at startup (<see cref="RowSecurityOptionsValidator"/>);
/// were it on regardless, every statement is denied rather than run unfiltered.
/// </summary>
internal sealed class RowSecurityResolver(IOptions<RowSecurityOptions> options) : IRowSecurityResolver
{
    public Task<RowSecurityContext> ResolveAsync(int dataSourceId, CancellationToken cancellationToken)
    {
        var context = options.Value.Enabled
            ? RowSecurityContext.Denied(RowSecurityContext.UnavailableReason)
            : RowSecurityContext.NotApplicable(RowSecurityContext.DisabledReason);

        return Task.FromResult(context);
    }
}
