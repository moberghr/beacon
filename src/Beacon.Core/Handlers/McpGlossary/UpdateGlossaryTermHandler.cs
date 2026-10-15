using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.McpGlossary;

/// <summary>
/// Edits a glossary term — its text, synonyms, definition, target column/metric, data-source scope, or
/// active flag. Only the fields supplied on the command are applied; the rest are left untouched. Throws
/// (§9.8) when the term does not exist. The next glossary re-index re-embeds the changed text. The edit is recorded in the
/// security audit with the names of the fields it supplied (never their text) in the same save.
/// </summary>
internal sealed class UpdateGlossaryTermHandler(IDbContextFactory<BeaconContext> contextFactory, ISecurityAuditRecorder securityAudit)
    : IRequestHandler<UpdateGlossaryTermCommand>
{
    public async Task Handle(UpdateGlossaryTermCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var term = await context.McpGlossaryTerms
            .Where(x => x.Id == request.Id)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Glossary term {request.Id} not found.");

        // null = leave unchanged; but a supplied blank for a required field is invalid (Create rejects
        // blank too — don't let a PUT silently blank Term/Definition).
        if (request.Term is not null && string.IsNullOrWhiteSpace(request.Term))
        {
            throw new InvalidOperationException("Glossary term text cannot be blank.");
        }

        if (request.Definition is not null && string.IsNullOrWhiteSpace(request.Definition))
        {
            throw new InvalidOperationException("Glossary term definition cannot be blank.");
        }

        if (request.Term != null)
        {
            term.Term = request.Term;
        }

        if (request.Synonyms != null)
        {
            term.Synonyms = request.Synonyms;
        }

        if (request.Definition != null)
        {
            term.Definition = request.Definition;
        }

        if (request.TargetSchema != null)
        {
            term.TargetSchema = request.TargetSchema;
        }

        if (request.TargetTable != null)
        {
            term.TargetTable = request.TargetTable;
        }

        if (request.TargetColumn != null)
        {
            term.TargetColumn = request.TargetColumn;
        }

        if (request.MetricExpression != null)
        {
            term.MetricExpression = request.MetricExpression;
        }

        if (request.DataSourceId.HasValue)
        {
            await ProjectDataSourceLinks.EnsureLinkedAsync(context, term.ProjectId, request.DataSourceId, cancellationToken);
            term.DataSourceId = request.DataSourceId.Value;
        }

        if (request.IsActive.HasValue)
        {
            term.IsActive = request.IsActive.Value;
        }

        await securityAudit.StageAsync(
            context,
            new SecurityAuditEntry(SecurityAuditActions.GlossaryTermUpdated)
                .On(SecurityAuditResources.GlossaryTerm, term.Id)
                .InProject(term.ProjectId)
                .With(new SecurityAuditDetails()
                    .AddCodes("fields", SuppliedFields(request))
                    .Add("dataSourceId", term.DataSourceId)
                    .Add("isActive", term.IsActive)),
            cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    // The fields the request supplied, by name: what the edit may have changed, never what it wrote.
    private static IEnumerable<string> SuppliedFields(UpdateGlossaryTermCommand request)
    {
        var supplied = new (string Field, bool Given)[]
        {
            ("term", request.Term != null),
            ("synonyms", request.Synonyms != null),
            ("definition", request.Definition != null),
            ("targetSchema", request.TargetSchema != null),
            ("targetTable", request.TargetTable != null),
            ("targetColumn", request.TargetColumn != null),
            ("metricExpression", request.MetricExpression != null),
            ("dataSourceId", request.DataSourceId.HasValue),
            ("isActive", request.IsActive.HasValue)
        };

        return supplied
            .Where(x => x.Given)
            .Select(x => x.Field);
    }
}

[ProjectScopeExempt("admin-gated curation (the glossary); the term's data source must be linked to its project", Policy = ProjectScopeExemptAttribute.AdminPolicy)]
public record UpdateGlossaryTermCommand : IRequest
{
    public required int Id { get; init; }
    public int? DataSourceId { get; init; }
    public string? Term { get; init; }
    public string? Synonyms { get; init; }
    public string? Definition { get; init; }
    public string? TargetSchema { get; init; }
    public string? TargetTable { get; init; }
    public string? TargetColumn { get; init; }
    public string? MetricExpression { get; init; }
    public bool? IsActive { get; init; }
}
