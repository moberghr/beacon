using MediatR;

namespace Semantico.Core.Handlers.Projects;

public record InstructDocumentationCommand(
    int SectionId,
    string Instruction) : IRequest<InstructDocumentationResult>;

public record InstructDocumentationResult(string UpdatedContent);
