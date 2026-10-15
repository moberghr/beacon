using Beacon.Core.Data.Entities;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.McpLearning;

/// <summary>A decision on a documentation patch as the security audit records it: the patch, its target kind and source — never its text.</summary>
internal static class DocumentationPatchAudit
{
    public static SecurityAuditEntry Decided(string action, McpDocumentationPatch patch)
    {
        return new SecurityAuditEntry(action)
            .On(SecurityAuditResources.DocumentationPatch, patch.Id)
            .InProject(patch.ProjectId)
            .With(new SecurityAuditDetails()
                .Add("targetType", patch.TargetType)
                .Add("dataSourceId", patch.DataSourceId));
    }
}
