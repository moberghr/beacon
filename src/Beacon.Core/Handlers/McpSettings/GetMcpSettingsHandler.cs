using MediatR;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.McpSettings;

internal sealed class GetMcpSettingsHandler(IMcpSettingsProvider settingsProvider)
    : IRequestHandler<GetMcpSettingsQuery, McpSettingsData>
{
    public async Task<McpSettingsData> Handle(GetMcpSettingsQuery request, CancellationToken cancellationToken)
    {
        return await settingsProvider.GetSettingsAsync(cancellationToken);
    }
}

[ProjectScopeExempt("security configuration, read by an Auditor or a SecurityAdmin", Policy = BeaconPermissions.AuditorOrSecurityAdminPolicy)]
public record GetMcpSettingsQuery : IRequest<McpSettingsData>;
