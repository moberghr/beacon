using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.McpGlossary;

/// <summary>
/// Creates an admin-managed business glossary term for a project (optionally scoped to a data source).
/// The term is embedded on the next glossary re-index (the Warp doc-chunk/glossary job) and its definition
/// is injected into the smart context when it is near a masked question (Tier-3 ⑪). Throws (§9.8) when the
/// term or definition is blank — a blank term produces a useless embedding. The creation is recorded in the security audit
/// (ids only, never the term's text) in the same unit of work.
/// </summary>
internal sealed class CreateGlossaryTermHandler(IDbContextFactory<BeaconContext> contextFactory, ISecurityAuditRecorder securityAudit)
    : IRequestHandler<CreateGlossaryTermCommand, CreateGlossaryTermResult>
{
    public async Task<CreateGlossaryTermResult> Handle(CreateGlossaryTermCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Term))
        {
            throw new InvalidOperationException("Glossary term text is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Definition))
        {
            throw new InvalidOperationException("Glossary term definition is required.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        await ProjectDataSourceLinks.EnsureLinkedAsync(context, request.ProjectId, request.DataSourceId, cancellationToken);

        var term = new McpGlossaryTerm
        {
            ProjectId = request.ProjectId,
            DataSourceId = request.DataSourceId,
            Term = request.Term,
            Synonyms = request.Synonyms,
            Definition = request.Definition,
            TargetSchema = request.TargetSchema,
            TargetTable = request.TargetTable,
            TargetColumn = request.TargetColumn,
            MetricExpression = request.MetricExpression,
            IsActive = true
        };

        context.McpGlossaryTerms.Add(term);
        await securityAudit.SaveWithEventsAsync(
            context,
            () =>
            [
                new SecurityAuditEntry(SecurityAuditActions.GlossaryTermCreated)
                    .On(SecurityAuditResources.GlossaryTerm, term.Id)
                    .InProject(term.ProjectId)
                    .With(new SecurityAuditDetails().Add("dataSourceId", term.DataSourceId))
            ],
            cancellationToken);

        return new CreateGlossaryTermResult(term.Id);
    }
}

[ProjectScopeExempt("admin-gated curation (the glossary); the term's data source must be linked to its project", Policy = ProjectScopeExemptAttribute.AdminPolicy)]
public record CreateGlossaryTermCommand : IRequest<CreateGlossaryTermResult>
{
    public required int ProjectId { get; init; }
    public int? DataSourceId { get; init; }
    public required string Term { get; init; }
    public string? Synonyms { get; init; }
    public required string Definition { get; init; }
    public string? TargetSchema { get; init; }
    public string? TargetTable { get; init; }
    public string? TargetColumn { get; init; }
    public string? MetricExpression { get; init; }
}

public record CreateGlossaryTermResult(int Id);
