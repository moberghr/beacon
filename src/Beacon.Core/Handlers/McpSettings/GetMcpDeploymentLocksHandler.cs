using MediatR;
using Microsoft.Extensions.Options;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;

namespace Beacon.Core.Handlers.McpSettings;

internal sealed class GetMcpDeploymentLocksHandler(IOptions<McpDeploymentOptions> deploymentOptions)
    : IRequestHandler<GetMcpDeploymentLocksQuery, GetMcpDeploymentLocksResult>
{
    public Task<GetMcpDeploymentLocksResult> Handle(GetMcpDeploymentLocksQuery request, CancellationToken cancellationToken)
    {
        var lockedFields = McpLockPolicy.ActiveRules(deploymentOptions.Value)
            .Select(x => x.FieldName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return Task.FromResult(new GetMcpDeploymentLocksResult(lockedFields));
    }
}

/// <summary>The MCP settings the deployment pins (<c>Beacon:Mcp</c>), for the global settings page. For an Auditor or a SecurityAdmin.</summary>
[ProjectScopeExempt("security configuration, read by an Auditor or a SecurityAdmin", Policy = BeaconPermissions.AuditorOrSecurityAdminPolicy)]
public record GetMcpDeploymentLocksQuery : IRequest<GetMcpDeploymentLocksResult>;

/// <param name="LockedFields">Settings a deployment lock pins: they cannot be changed globally or per project, by anyone.</param>
public record GetMcpDeploymentLocksResult(string[] LockedFields);
