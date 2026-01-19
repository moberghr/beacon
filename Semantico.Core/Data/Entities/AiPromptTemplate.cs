using Semantico.Core.Data.Entities.Base;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Data.Entities;

public class AiPromptTemplate : BaseEntity
{
    public string Name { get; set; } = null!;
    public OperationType OperationType { get; set; }
    public string PromptTemplate { get; set; } = null!;
    public string? SystemPrompt { get; set; }
    public int Version { get; set; }
    public bool IsActive { get; set; }
    public decimal Temperature { get; set; }
    public int MaxTokens { get; set; }
    public string VariableDefinitions { get; set; } = null!;
    public string? Description { get; set; }
    public string CreatedBy { get; set; } = null!;
    public string ModifiedBy { get; set; } = null!;
}
