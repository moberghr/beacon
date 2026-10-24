using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Authorization;
using Beacon.Core.Helpers;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.McpEval;

/// <summary>
/// Lists golden eval cases, optionally scoped by project and/or data source, with who promoted and who last edited each
/// (named as <see cref="UserLabel"/> names users to one another), and whether a prompt leaves each out: gold SQL that cannot
/// be shown without its comments in the data source's dialect, or a question holding characters a reviewer cannot see.
/// </summary>
internal sealed class GetGoldenCasesHandler(IDbContextFactory<BeaconContext> contextFactory,
    LegacyListTruncation? truncation = null,
    ILogger<GetGoldenCasesHandler>? logger = null)
    : IRequestHandler<GetGoldenCasesQuery, List<GoldenCaseItem>>
{
    public async Task<List<GoldenCaseItem>> Handle(GetGoldenCasesQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = context.McpEvalCases.AsQueryable();
        if (request.ProjectId.HasValue)
        {
            query = query.Where(x => x.ProjectId == request.ProjectId.Value);
        }

        if (request.DataSourceId.HasValue)
        {
            query = query.Where(x => x.DataSourceId == request.DataSourceId.Value);
        }

        var items = await query
            .OrderByDescending(x => x.CreatedTime)
            .Take(ListRequest.LegacyUnpagedCap + 1)
            .Select(x =>
                new GoldenCaseItem
                {
                    Id = x.Id,
                    ProjectId = x.ProjectId,
                    DataSourceId = x.DataSourceId,
                    Question = x.Question,
                    GoldSql = x.GoldSql,
                    SourceSignalId = x.SourceSignalId,
                    IsActive = x.IsActive,
                    Notes = x.Notes,
                    CreatedTime = x.CreatedTime,
                    PromotedByUserId = x.PromotedByUserId,
                    PromotedByName = context.Users
                        .Where(y => y.Id == x.PromotedByUserId)
                        .Select(UserLabel.OfUser)
                        .FirstOrDefault(),
                    ModifiedByUserId = x.ModifiedByUserId,
                    ModifiedByName = context.Users
                        .Where(y => y.Id == x.ModifiedByUserId)
                        .Select(UserLabel.OfUser)
                        .FirstOrDefault()
                })
            .ToListAsync(cancellationToken);
        items = LegacyListTruncation.Apply(items, truncation, logger, "golden cases");

        var dialects = await GroundingDialects.OfAsync(
            context,
            items
                .Select(x => x.DataSourceId)
                .Distinct()
                .ToList(),
            cancellationToken);

        return items
            .Select(x => x with
            {
                GoldSqlWithheld = GroundingDialects.Withholds(x.GoldSql, dialects.GetValueOrDefault(x.DataSourceId)),
                QuestionWithheld = GroundingText.HasHiddenCharacters(x.Question)
            })
            .ToList();
    }
}

[ProjectScopeExempt("admin-gated curation (the eval harness)", Policy = ProjectScopeExemptAttribute.AdminPolicy)]
public record GetGoldenCasesQuery : IRequest<List<GoldenCaseItem>>
{
    public int? ProjectId { get; init; }
    public int? DataSourceId { get; init; }
}

public record GoldenCaseItem
{
    public int Id { get; init; }
    public int ProjectId { get; init; }
    public int DataSourceId { get; init; }
    public string Question { get; init; } = "";
    public string GoldSql { get; init; } = "";
    public int? SourceSignalId { get; init; }
    public bool IsActive { get; init; }
    public string? Notes { get; init; }
    public DateTime CreatedTime { get; init; }

    /// <summary>The <c>Users.Id</c> of the user who promoted the case; null for a case created before Beacon recorded it.</summary>
    public int? PromotedByUserId { get; init; }

    /// <summary>The promoting user's display name (or "User {id}"); null when no promoting user is recorded.</summary>
    public string? PromotedByName { get; init; }

    /// <summary>The <c>Users.Id</c> of the user who last edited the case; null until it is edited.</summary>
    public int? ModifiedByUserId { get; init; }

    /// <summary>The last editor's display name (or "User {id}"); null until the case is edited.</summary>
    public string? ModifiedByName { get; init; }

    /// <summary>
    /// True when a prompt leaves the case out because its gold SQL cannot be shown without its comments in the data source's
    /// dialect (a comment that cannot be removed safely, or characters a reviewer cannot see). Edit the SQL to bring it back.
    /// </summary>
    public bool GoldSqlWithheld { get; init; }

    /// <summary>True when a prompt leaves the case out because its question holds characters a reviewer cannot see.</summary>
    public bool QuestionWithheld { get; init; }
}
