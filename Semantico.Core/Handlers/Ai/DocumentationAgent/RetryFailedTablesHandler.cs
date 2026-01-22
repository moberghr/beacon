using MediatR;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Ai;
using Semantico.Core.Data.Entities;



namespace Semantico.Core.Handlers.Ai.DocumentationAgent;

public record RetryFailedTablesCommand : IRequest<RetryFailedTablesResult>
{
    public int AgentRunId { get; set; }
}

// Response
public record RetryFailedTablesResult
{
    public int AgentRunId { get; set; }
    public int FailedTableCount { get; set; }
}
