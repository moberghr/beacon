using MediatR;

namespace Beacon.Core.Handlers.Projects;

public record GetProjectMcpContextQuery(int ProjectId) : IRequest<GetProjectMcpContextResult>;

public record GetProjectMcpContextResult(string Context);
