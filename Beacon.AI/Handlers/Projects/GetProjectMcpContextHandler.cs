using MediatR;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Handlers.Projects;

namespace Beacon.AI.Handlers.Projects;

internal sealed class GetProjectMcpContextHandler(IKnowledgeGraphService knowledgeGraphService)
    : IRequestHandler<GetProjectMcpContextQuery, GetProjectMcpContextResult>
{
    public async Task<GetProjectMcpContextResult> Handle(GetProjectMcpContextQuery request, CancellationToken cancellationToken)
    {
        var context = await knowledgeGraphService.GetProjectContextForLlmAsync(request.ProjectId, cancellationToken);
        return new GetProjectMcpContextResult(context);
    }
}
