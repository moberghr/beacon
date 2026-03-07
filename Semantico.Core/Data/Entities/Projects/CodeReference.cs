using Semantico.Core.Data.Entities.Base;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Data.Entities.Projects;

public class CodeReference : BaseEntity
{
    public int GitHubRepositoryId { get; set; }
    public required string FilePath { get; set; }
    public int? LineNumber { get; set; }
    public CodeReferenceType ReferenceType { get; set; }

    public string? SchemaName { get; set; }
    public string? TableName { get; set; }
    public string? ColumnName { get; set; }

    public string? CodeSnippet { get; set; }
    public string? ClassName { get; set; }
    public string? MethodName { get; set; }

    public GitHubRepository GitHubRepository { get; set; } = null!;
}
