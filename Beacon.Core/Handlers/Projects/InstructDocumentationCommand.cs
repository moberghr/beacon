using MediatR;

namespace Beacon.Core.Handlers.Projects;

public record InstructDocumentationCommand(
    int SectionId,
    string Instruction) : IRequest<InstructDocumentationResult>;

public record InstructDocumentationResult(string UpdatedContent);
