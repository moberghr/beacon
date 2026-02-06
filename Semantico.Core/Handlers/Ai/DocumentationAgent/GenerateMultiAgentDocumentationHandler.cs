using MediatR;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Ai;
using Semantico.Core.Models.Ai.MultiAgent;
using Semantico.Core.Data.Entities;

namespace Semantico.Core.Handlers.Ai.DocumentationAgent;

public record GenerateMultiAgentDocumentationCommand : IRequest<DataSourceDocumentation>
{
    public int DataSourceId { get; init; }
    public int UserId { get; init; }
    public int? MaxConcurrentAgents { get; init; }
    public List<string>? SpecificTables { get; init; }
    public List<string>? ExcludedTables { get; init; }
    public int? MaxTables { get; init; }
    public string? Title { get; init; }
    public IProgress<DocumentationProgress>? Progress { get; init; }
}
