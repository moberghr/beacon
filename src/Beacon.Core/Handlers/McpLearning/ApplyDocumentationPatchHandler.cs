using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Authorization;
using Beacon.Core.Models;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.McpLearning;

/// <summary>
/// Applies a proposed documentation patch. A section patch appends its content to the project's latest documentation
/// section of that title and marks the section curated (edited by the applying user, at the time of applying). A column or table patch replaces the data source's description, which every project linking
/// the source sees, so it applies only while the description still holds the content captured when the patch was
/// proposed (<see cref="McpDocumentationPatch.CurrentContent"/>, compared exactly; null and empty are different values, as
/// the proposal captured them): a description changed since, by hand or by another project's patch, refuses the patch
/// (409) and leaves it proposed. The description is written in one statement conditional on that content, and the
/// patch's status in one conditional on it still being proposed, in one transaction with the audit event, so an apply
/// racing another write changes nothing. The conditional statement compares under the database's collation; the exact
/// comparison is made on the content read just before it.
/// </summary>
internal sealed class ApplyDocumentationPatchHandler(IDbContextFactory<BeaconContext> contextFactory, ISecurityAuditRecorder securityAudit)
    : IRequestHandler<ApplyDocumentationPatchCommand>
{
    internal const string TargetChangedMessage =
        "The target changed since the patch was proposed: reject the patch and let it be proposed again.";

    public async Task Handle(ApplyDocumentationPatchCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var patch = await context.McpDocumentationPatches.FindAsync([request.PatchId], cancellationToken)
            ?? throw new InvalidOperationException($"Patch {request.PatchId} not found");

        if (patch.Status != McpDocPatchStatus.Proposed)
            throw new InvalidOperationException($"Patch {request.PatchId} is already {patch.Status}");

        if (patch.TargetType == McpDocPatchTarget.DocumentationSection)
        {
            await ApplyToSectionAsync(context, patch, request, cancellationToken);

            return;
        }

        // A target that can't be located leaves the patch in Proposed, so it stays retryable rather than silently
        // marking it Applied.
        var target = patch.TargetType switch
        {
            McpDocPatchTarget.ColumnDescription => await FindColumnAsync(context, patch, cancellationToken),
            McpDocPatchTarget.TableDescription => await FindTableAsync(context, patch, cancellationToken),
            _ => null
        } ?? throw TargetNotFound(patch);

        if (!string.Equals(target.Content, patch.CurrentContent, StringComparison.Ordinal))
        {
            throw new BeaconConflictException(TargetChangedMessage);
        }

        var appliedAt = DateTime.UtcNow;
        var written = await securityAudit.SaveWithEventsAsync(
            context,
            async () =>
            {
                var changed = await WriteDescriptionAsync(context, patch, target.Id, cancellationToken);
                if (changed == 0)
                {
                    return 0;
                }

                var decided = await context.McpDocumentationPatches
                    .Where(x => x.Id == patch.Id)
                    .Where(x => x.Status == McpDocPatchStatus.Proposed)
                    .ExecuteUpdateAsync(
                        x => x
                            .SetProperty(y => y.Status, McpDocPatchStatus.Applied)
                            .SetProperty(y => y.AppliedByUserId, request.AppliedByUserId)
                            .SetProperty(y => y.AppliedAt, appliedAt),
                        cancellationToken);
                if (decided == 0)
                {
                    // Decided by someone else meanwhile: the description written above rolls back with the transaction.
                    throw new BeaconConflictException($"Patch {request.PatchId} was decided meanwhile.");
                }

                return changed;
            },
            changed => changed == 0
                ? []
                : [DocumentationPatchAudit.Decided(SecurityAuditActions.DocumentationPatchApplied, patch)],
            cancellationToken);
        if (written == 0)
        {
            throw new BeaconConflictException(TargetChangedMessage);
        }
    }

    private async Task ApplyToSectionAsync(BeaconContext context, McpDocumentationPatch patch, ApplyDocumentationPatchCommand request, CancellationToken ct)
    {
        var section = await FindDocumentationSectionAsync(context, patch, ct) ?? throw TargetNotFound(patch);

        patch.Status = McpDocPatchStatus.Applied;
        patch.AppliedByUserId = request.AppliedByUserId;
        patch.AppliedAt = DateTime.UtcNow;

        // A curator's decision changes the section like an edit by hand: it is curated from now on (regeneration keeps it).
        section.Content += "\n\n" + patch.ProposedContent;
        section.EditedByUserId = request.AppliedByUserId;
        section.EditedTime = patch.AppliedAt;

        await securityAudit.StageAsync(context, DocumentationPatchAudit.Decided(SecurityAuditActions.DocumentationPatchApplied, patch), ct);
        await context.SaveChangesAsync(ct);
    }

    private static InvalidOperationException TargetNotFound(McpDocumentationPatch patch)
    {
        return new InvalidOperationException(
            $"Patch {patch.Id} target '{patch.TargetIdentifier}' was not found; patch remains retryable");
    }

    private static async Task<DescriptionTarget?> FindColumnAsync(BeaconContext context, McpDocumentationPatch patch, CancellationToken ct)
    {
        // TargetIdentifier format: "schema.table.column"
        var parts = patch.TargetIdentifier.Split('.');
        if (parts.Length < 3)
        {
            return null;
        }

        var schema = parts[0];
        var table = parts[1];
        var column = string.Join('.', parts.Skip(2));

        return await context.ColumnMetadata
            .Where(x => x.DatabaseMetadata.DataSourceId == patch.DataSourceId)
            .Where(x => x.DatabaseMetadata.SchemaName == schema)
            .Where(x => x.DatabaseMetadata.TableName == table)
            .Where(x => x.ColumnName == column)
            .Select(x => new DescriptionTarget(x.Id, x.Description))
            .FirstOrDefaultAsync(ct);
    }

    private static async Task<DescriptionTarget?> FindTableAsync(BeaconContext context, McpDocumentationPatch patch, CancellationToken ct)
    {
        // TargetIdentifier format: "schema.table"
        var parts = patch.TargetIdentifier.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        var schema = parts[0];
        var table = parts[1];

        return await context.DatabaseMetadata
            .Where(x => x.DataSourceId == patch.DataSourceId)
            .Where(x => x.SchemaName == schema)
            .Where(x => x.TableName == table)
            .Select(x => new DescriptionTarget(x.Id, x.TableDescription))
            .FirstOrDefaultAsync(ct);
    }

    // Writes the proposed description only while the target still holds the content captured at proposal.
    private static Task<int> WriteDescriptionAsync(BeaconContext context, McpDocumentationPatch patch, int targetId, CancellationToken ct)
    {
        var captured = patch.CurrentContent;
        var proposed = patch.ProposedContent;

        return patch.TargetType == McpDocPatchTarget.ColumnDescription
            ? context.ColumnMetadata
                .Where(x => x.Id == targetId)
                .Where(x => x.Description == captured)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.Description, proposed), ct)
            : context.DatabaseMetadata
                .Where(x => x.Id == targetId)
                .Where(x => x.TableDescription == captured)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.TableDescription, proposed), ct);
    }

    private static async Task<ProjectDocumentationSection?> FindDocumentationSectionAsync(BeaconContext context, McpDocumentationPatch patch, CancellationToken ct)
    {
        // TargetIdentifier format: "ProjectDocumentation:{docId}:SectionType:{sectionType}"
        // Or simpler: find the latest doc section of the given type for this project
        var latestDoc = await context.ProjectDocumentations
            .Where(d => d.ProjectId == patch.ProjectId)
            .OrderByDescending(d => d.GeneratedAt)
            .FirstOrDefaultAsync(ct);

        if (latestDoc == null)
        {
            return null;
        }

        return await context.ProjectDocumentationSections
            .FirstOrDefaultAsync(s =>
                s.ProjectDocumentationId == latestDoc.Id &&
                s.Title == patch.TargetIdentifier, ct);
    }

    private sealed record DescriptionTarget(int Id, string? Content);
}

public record ApplyDocumentationPatchCommand : IRequest, IProjectAuthorizedRequest
{
    public required int PatchId { get; init; }
    [ProjectScopeExempt("the caller, set by the endpoint; not a project resource")]
    public int? AppliedByUserId { get; init; }

    // A patch to a documentation section: Curate in the patch's project. A patch to a column or table description: Write in
    // the patch's project and Curate in every live project linking its data source, or a DataSourceAdmin.
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        ProjectCheck.CurateShared(ProjectResource.DocumentationPatch(PatchId))
    ];
}
