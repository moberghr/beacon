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

[ProjectScopeExempt("admin-gated security configuration", Policy = ProjectScopeExemptAttribute.AdminPolicy)]
public record GetMcpSettingsQuery : IRequest<McpSettingsData>;
