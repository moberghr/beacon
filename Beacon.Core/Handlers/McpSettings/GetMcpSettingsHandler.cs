using MediatR;
using Beacon.Core.Models;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.McpSettings;

internal sealed class GetMcpSettingsHandler(IMcpSettingsProvider settingsProvider)
    : IRequestHandler<GetMcpSettingsQuery, McpSettingsData>
{
    public async Task<McpSettingsData> Handle(GetMcpSettingsQuery request, CancellationToken cancellationToken)
    {
        return await settingsProvider.GetSettingsAsync(cancellationToken);
    }
}

public record GetMcpSettingsQuery : IRequest<McpSettingsData>;
