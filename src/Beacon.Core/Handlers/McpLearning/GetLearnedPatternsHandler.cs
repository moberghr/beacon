using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Services;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;

namespace Beacon.Core.Handlers.McpLearning;

internal sealed class GetLearnedPatternsHandler(IDbContextFactory<BeaconContext> contextFactory, IProjectAccessEvaluator projectAccess)
    : IRequestHandler<GetLearnedPatternsQuery, PagedList<LearnedPatternEntry>>
{
    public async Task<PagedList<LearnedPatternEntry>> Handle(GetLearnedPatternsQuery request, CancellationToken cancellationToken)
    {
        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        var readable = scope.For(ProjectAction.Read);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = context.McpLearnedPatterns
            .WhereProjectIn(readable, x => x.ProjectId);

        if (request.ProjectId.HasValue)
            query = query.Where(p => p.ProjectId == request.ProjectId.Value);

        if (request.DataSourceId.HasValue)
            query = query.Where(p => p.DataSourceId == request.DataSourceId.Value);

        if (request.Status.HasValue)
            query = query.Where(p => p.Status == request.Status.Value);

        if (request.PatternType.HasValue)
            query = query.Where(p => p.PatternType == request.PatternType.Value);

        if (!string.IsNullOrEmpty(request.TableName))
            query = query.Where(p => p.TableName == request.TableName);

        var page = await query
            .Select(p => new LearnedPatternEntry
            {
                Id = p.Id,
                ProjectId = p.ProjectId,
                DataSourceId = p.DataSourceId,
                SchemaName = p.SchemaName,
                TableName = p.TableName,
                ColumnName = p.ColumnName,
                PatternType = p.PatternType,
                PatternContent = p.PatternContent,
                ExampleQuestion = p.ExampleQuestion,
                ExampleSql = p.ExampleSql,
                SignalCount = p.SignalCount,
                Confidence = p.Confidence,
                Status = p.Status,
                CreatedTime = p.CreatedTime,
                LastRefreshedAt = p.LastRefreshedAt,
                ReviewedByName = context.Users
                    .Where(x => x.Id == p.ReviewedByUserId)
                    .Select(UserLabel.OfUser)
                    .FirstOrDefault(),
                ReviewedAt = p.ReviewedAt
            })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "-confidence,-signalCount");

        var dialects = await GroundingDialects.OfAsync(
            context,
            page.Items
                .Where(x => !string.IsNullOrWhiteSpace(x.ExampleSql))
                .Select(x => x.DataSourceId)
                .Distinct()
                .ToList(),
            cancellationToken);

        return page.Map(x => x with
        {
            CanCurate = GroundingCuration.MayCurateProject(scope, x.ProjectId),
            ExampleSqlWithheld = GroundingDialects.Withholds(x.ExampleSql, dialects.GetValueOrDefault(x.DataSourceId)),
            ExampleQuestionWithheld = GroundingText.HasHiddenCharacters(x.ExampleQuestion)
        });
    }
}

/// <summary>Most confident first (then most signals) unless <c>sort</c> says otherwise.</summary>
public record GetLearnedPatternsQuery : ListRequest, IRequest<PagedList<LearnedPatternEntry>>, IProjectFilteredRequest
{
    [ProjectScopeExempt("filter only: the list is scope-filtered")]
    public int? ProjectId { get; init; }
    [ProjectScopeExempt("filter only: the list is scope-filtered")]
    public int? DataSourceId { get; init; }
    public McpPatternStatus? Status { get; init; }
    public McpPatternType? PatternType { get; init; }
    public string? TableName { get; init; }
}

public record LearnedPatternEntry
{
    public int Id { get; init; }
    public int ProjectId { get; init; }
    public int DataSourceId { get; init; }
    public string SchemaName { get; init; } = "";
    public string TableName { get; init; } = "";
    public string? ColumnName { get; init; }
    public McpPatternType PatternType { get; init; }
    public string PatternContent { get; init; } = "";
    public string? ExampleQuestion { get; init; }
    public string? ExampleSql { get; init; }
    public int SignalCount { get; init; }
    public double Confidence { get; init; }
    public McpPatternStatus Status { get; init; }
    public DateTime CreatedTime { get; init; }
    public DateTime? LastRefreshedAt { get; init; }

    /// <summary>
    /// Who last set the pattern's review status — display name, or "User {id}" (<see cref="UserLabel"/>); null when no person
    /// reviewed it.
    /// </summary>
    public string? ReviewedByName { get; init; }

    public DateTime? ReviewedAt { get; init; }

    /// <summary>Whether the caller may approve or reject the pattern: Curate in its project (the project admin role).</summary>
    public bool CanCurate { get; init; }

    /// <summary>
    /// True when a prompt leaves the example SQL out (the lesson stays): it cannot be shown without its comments in the data
    /// source's dialect, or holds characters a reviewer cannot see.
    /// </summary>
    public bool ExampleSqlWithheld { get; init; }

    /// <summary>True when a prompt leaves the example question out (the lesson stays): it holds characters a reviewer cannot see.</summary>
    public bool ExampleQuestionWithheld { get; init; }

    /// <summary>
    /// The SHA-256 of what a reviewer decides on — <see cref="PatternContent"/>, <see cref="ExampleQuestion"/> and
    /// <see cref="ExampleSql"/> (lowercase hex). A status change sends it back, and is refused (409) when the pattern's content
    /// hashes otherwise by then.
    /// </summary>
    public string ContentHash => GroundingContentHash.OfPattern(PatternContent, ExampleQuestion, ExampleSql);
}
