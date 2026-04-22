using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities;

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
    public string? Description { get; set; }
    public string CreatedBy { get; set; } = null!;
    public string ModifiedBy { get; set; } = null!;
}
