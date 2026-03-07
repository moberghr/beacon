using MediatR;
using Semantico.Core.Models;
using Semantico.Core.Services;

namespace Semantico.Core.Handlers.McpSettings;

internal sealed class GetMcpSettingsHandler(IMcpSettingsProvider settingsProvider)
    : IRequestHandler<GetMcpSettingsQuery, McpSettingsData>
{
    public async Task<McpSettingsData> Handle(GetMcpSettingsQuery request, CancellationToken cancellationToken)
    {
        return await settingsProvider.GetSettingsAsync(cancellationToken);
    }
}

public record GetMcpSettingsQuery : IRequest<McpSettingsData>;
