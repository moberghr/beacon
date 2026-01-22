using MediatR;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Ai;
using Semantico.Core.Data.Entities;



namespace Semantico.Core.Handlers.Ai.DocumentationAgent;

public record CancelDocumentationAgentCommand : IRequest
{
    public int AgentRunId { get; init; }
}
