using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Handlers.McpLearning;

internal sealed class ApplyDocumentationPatchHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<ApplyDocumentationPatchCommand>
{
    public async Task Handle(ApplyDocumentationPatchCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var patch = await context.McpDocumentationPatches.FindAsync([request.PatchId], cancellationToken)
            ?? throw new InvalidOperationException($"Patch {request.PatchId} not found");

        if (patch.Status != McpDocPatchStatus.Proposed)
            throw new InvalidOperationException($"Patch {request.PatchId} is already {patch.Status}");

        // Apply the patch to the target entity. If the target can't be located, leave the patch
        // in Proposed so it stays retryable rather than silently marking it Applied.
        var applied = patch.TargetType switch
        {
            McpDocPatchTarget.ColumnDescription => await ApplyColumnDescriptionAsync(context, patch, cancellationToken),
            McpDocPatchTarget.TableDescription => await ApplyTableDescriptionAsync(context, patch, cancellationToken),
            McpDocPatchTarget.DocumentationSection => await ApplyDocumentationSectionAsync(context, patch, cancellationToken),
            _ => false
        };

        if (!applied)
        {
            throw new InvalidOperationException(
                $"Patch {request.PatchId} target '{patch.TargetIdentifier}' was not found; patch remains retryable");
        }

        patch.Status = McpDocPatchStatus.Applied;
        patch.AppliedByUserId = request.AppliedByUserId;
        patch.AppliedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(cancellationToken);
    }

    private static async Task<bool> ApplyColumnDescriptionAsync(BeaconContext context, Data.Entities.McpDocumentationPatch patch, CancellationToken ct)
    {
        // TargetIdentifier format: "schema.table.column"
        var parts = patch.TargetIdentifier.Split('.');
        if (parts.Length < 3)
        {
            return false;
        }

        var schema = parts[0];
        var table = parts[1];
        var column = string.Join('.', parts.Skip(2));

        var col = await context.ColumnMetadata
            .FirstOrDefaultAsync(c =>
                c.DatabaseMetadata.DataSourceId == patch.DataSourceId &&
                c.DatabaseMetadata.SchemaName == schema &&
                c.DatabaseMetadata.TableName == table &&
                c.ColumnName == column, ct);

        if (col == null)
        {
            return false;
        }

        col.Description = patch.ProposedContent;
        return true;
    }

    private static async Task<bool> ApplyTableDescriptionAsync(BeaconContext context, Data.Entities.McpDocumentationPatch patch, CancellationToken ct)
    {
        // TargetIdentifier format: "schema.table"
        var parts = patch.TargetIdentifier.Split('.');
        if (parts.Length < 2)
        {
            return false;
        }

        var metadata = await context.DatabaseMetadata
            .FirstOrDefaultAsync(m =>
                m.DataSourceId == patch.DataSourceId &&
                m.SchemaName == parts[0] &&
                m.TableName == parts[1], ct);

        if (metadata == null)
        {
            return false;
        }

        metadata.TableDescription = patch.ProposedContent;
        return true;
    }

    private static async Task<bool> ApplyDocumentationSectionAsync(BeaconContext context, Data.Entities.McpDocumentationPatch patch, CancellationToken ct)
    {
        // TargetIdentifier format: "ProjectDocumentation:{docId}:SectionType:{sectionType}"
        // Or simpler: find the latest doc section of the given type for this project
        var latestDoc = await context.ProjectDocumentations
            .Where(d => d.ProjectId == patch.ProjectId)
            .OrderByDescending(d => d.GeneratedAt)
            .FirstOrDefaultAsync(ct);

        if (latestDoc == null)
        {
            return false;
        }

        var section = await context.ProjectDocumentationSections
            .FirstOrDefaultAsync(s =>
                s.ProjectDocumentationId == latestDoc.Id &&
                s.Title == patch.TargetIdentifier, ct);

        if (section == null)
        {
            return false;
        }

        section.Content += "\n\n" + patch.ProposedContent;
        return true;
    }
}

public record ApplyDocumentationPatchCommand : IRequest
{
    public required int PatchId { get; init; }
    public int? AppliedByUserId { get; init; }
}
