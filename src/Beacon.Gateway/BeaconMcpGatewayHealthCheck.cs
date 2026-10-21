using Microsoft.Extensions.Diagnostics.HealthChecks;
using Beacon.Core.HostData;

namespace Beacon.Gateway;

/// <summary>
/// Healthy once the host project has been synced; Degraded until then, with the failure count and the last failure's type.
/// Asking also starts (or retries, once its back-off has passed) the sync, so a readiness probe brings the gateway up
/// without a caller having to arrive first.
/// </summary>
internal sealed class BeaconMcpGatewayHealthCheck(IBeaconHostProjectGate hostProject) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (await hostProject.GetProjectIdAsync(cancellationToken) != null)
        {
            return HealthCheckResult.Healthy("The Beacon host project is synced.");
        }

        var state = hostProject.State;
        var data = new Dictionary<string, object>
        {
            ["failures"] = state.Failures
        };
        if (state.LastError != null)
        {
            data["lastError"] = state.LastError;
        }

        if (state.NextAttemptAt != null)
        {
            data["nextAttemptAt"] = state.NextAttemptAt.Value;
        }

        return new HealthCheckResult(
            context.Registration.FailureStatus,
            "The Beacon host project is not synced yet; MCP callers are refused until it is.",
            data: data);
    }
}
