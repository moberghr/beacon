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
        var activeRules = McpLockPolicy.ActiveRules(deploymentOptions.Value);
        var lockedFields = activeRules
            .Select(x => x.FieldName)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var locks = activeRules
            .Select(x => new McpDeploymentLock(x.FieldName, x.LockName))
            .OrderBy(x => x.FieldName, StringComparer.Ordinal)
            .ThenBy(x => x.LockName, StringComparer.Ordinal)
            .ToArray();

        return Task.FromResult(new GetMcpDeploymentLocksResult(lockedFields) { Locks = locks });
    }
}

/// <summary>The MCP settings the deployment pins (<c>Beacon:Mcp</c>), for the global settings page. For an Auditor or a SecurityAdmin.</summary>
[ProjectScopeExempt("security configuration, read by an Auditor or a SecurityAdmin", Policy = BeaconPermissions.AuditorOrSecurityAdminPolicy)]
public record GetMcpDeploymentLocksQuery : IRequest<GetMcpDeploymentLocksResult>;

/// <param name="LockedFields">Settings a deployment lock pins: they cannot be changed globally or per project, by anyone.</param>
public record GetMcpDeploymentLocksResult(string[] LockedFields)
{
    /// <summary>Each active lock and the setting it pins (<c>Audit:RequireContent</c> pins <c>RetainQueryContent</c> on).</summary>
    public McpDeploymentLock[] Locks { get; init; } = [];
}

/// <summary>An active deployment lock (<c>Beacon:Mcp</c>) and the setting it pins.</summary>
public record McpDeploymentLock(string FieldName, string LockName);
