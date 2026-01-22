using MediatR;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Ai;
using Semantico.Core.Data.Entities;



namespace Semantico.Core.Handlers.Ai.DocumentationAgent;

public record StartDocumentationAgentCommand : IRequest<StartDocumentationAgentResult>
{
    public int DataSourceId { get; init; }
    public int UserId { get; init; }
    public DocumentationAgentOptions? Options { get; init; }
}

public record StartDocumentationAgentResult
{
    public int AgentRunId { get; init; }
    public int? DocumentationId { get; init; }
    public DocumentationAgentStatus Status { get; init; }
    public string Message { get; init; } = null!;
}
