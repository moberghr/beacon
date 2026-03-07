using Semantico.Core.Data.Entities.Base;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Data.Entities.Projects;

public class GitHubRepository : BaseEntity
{
    public int ProjectId { get; set; }
    public required string RepositoryUrl { get; set; }
    public string Branch { get; set; } = "main";
    public string? EncryptedAccessToken { get; set; }

    public DateTime? LastScanAt { get; set; }
    public ScanStatus ScanStatus { get; set; } = ScanStatus.Pending;
    public string? ScanCronExpression { get; set; }
    public string? LastScanError { get; set; }
    public int TotalFilesScanned { get; set; }
    public int TotalReferencesFound { get; set; }

    public Project Project { get; set; } = null!;
    public List<CodeReference> CodeReferences { get; set; } = new();
}
