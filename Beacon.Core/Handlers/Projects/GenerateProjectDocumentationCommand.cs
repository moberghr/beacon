using MediatR;

namespace Beacon.Core.Handlers.Projects;

public record GenerateProjectDocumentationCommand(int ProjectId, int UserId) : IRequest<GenerateProjectDocumentationResult>;

public record GenerateProjectDocumentationResult(string JobId);
