using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.McpEval;

/// <summary>
/// The golden candidates an Admin reviews: answers to questions (<c>ask</c> signals — a query or dry_run signal's
/// "question" is SQL) rated <see cref="McpUserVerdict.Correct"/> that were not promoted yet, scoped to a project and one data
/// source, whose content was retained (a question and SQL) — newest first. Each shows the SQL a promotion would start from
/// (the rater's corrected SQL, else the signal's corrected SQL, else its generated SQL) with its hash
/// (<see cref="GoldenCandidateItem.SqlHash"/>, which the promotion sends back), the caller who asked and the user who rated
/// it (named as <see cref="UserLabel"/> names users to one another). A candidate becomes a golden case only through
/// <see cref="PromoteSignalToGoldenCommand"/>.
/// </summary>
internal sealed class GetGoldenCandidatesHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetGoldenCandidatesQuery, PagedList<GoldenCandidateItem>>
{
    private const string AskTool = "ask";

    public async Task<PagedList<GoldenCandidateItem>> Handle(GetGoldenCandidatesQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = context.McpQuerySignals
            .Where(x => x.UserVerdict == McpUserVerdict.Correct)
            .Where(x => x.Tool == AskTool)
            .Where(x => x.PromotedAt == null)
            .Where(x => x.ProjectId != null)
            .Where(x => x.DataSourceId != null)
            .Where(x => x.Question != "")
            .Where(x => (x.UserCorrectedSql ?? x.CorrectedSql ?? x.GeneratedSql) != null)
            .Where(x => !context.McpEvalCases.Any(y => y.SourceSignalId == x.Id));

        if (request.ProjectId.HasValue)
        {
            query = query.Where(x => x.ProjectId == request.ProjectId.Value);
        }

        return await query
            .Select(x =>
                new GoldenCandidateItem
                {
                    SignalId = x.Id,
                    ProjectId = x.ProjectId!.Value,
                    DataSourceId = x.DataSourceId!.Value,
                    Question = x.Question,
                    Sql = (x.UserCorrectedSql ?? x.CorrectedSql ?? x.GeneratedSql)!,
                    IsUserCorrected = x.UserCorrectedSql != null,
                    FeedbackNote = x.FeedbackNote,
                    AskedByUserId = x.UserId,
                    AskedByName = context.Users
                        .Where(y => y.Id == x.UserId)
                        .Select(UserLabel.OfUser)
                        .FirstOrDefault(),
                    RatedByUserId = x.FeedbackByUserId,
                    RatedByName = context.Users
                        .Where(y => y.Id == x.FeedbackByUserId)
                        .Select(UserLabel.OfUser)
                        .FirstOrDefault(),
                    CreatedTime = x.CreatedTime
                })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "-createdTime", tiebreaker: nameof(GoldenCandidateItem.SignalId));
    }
}

[ProjectScopeExempt("admin-gated curation (the eval harness)", Policy = ProjectScopeExemptAttribute.AdminPolicy)]
public record GetGoldenCandidatesQuery : ListRequest, IRequest<PagedList<GoldenCandidateItem>>
{
    public int? ProjectId { get; init; }
}

public record GoldenCandidateItem
{
    public int SignalId { get; init; }
    public int ProjectId { get; init; }
    public int DataSourceId { get; init; }
    public string Question { get; init; } = "";

    /// <summary>The SQL a promotion starts from; the promotion checks it again and stores it without comments.</summary>
    public string Sql { get; init; } = "";

    /// <summary>
    /// The SHA-256 of <see cref="Sql"/> (lowercase hex). A promotion sends it back as what the Admin reviewed, and is refused
    /// (409) when the candidate's SQL hashes otherwise by then.
    /// </summary>
    public string SqlHash => GroundingContentHash.OfSql(Sql);

    /// <summary>True when <see cref="Sql"/> is the corrected SQL the rater supplied.</summary>
    public bool IsUserCorrected { get; init; }

    public string? FeedbackNote { get; init; }

    /// <summary>The <c>Users.Id</c> of the caller who asked; null when the signal recorded none.</summary>
    public int? AskedByUserId { get; init; }

    public string? AskedByName { get; init; }

    /// <summary>The <c>Users.Id</c> of the user who rated the answer; null for a verdict recorded before Beacon recorded it.</summary>
    public int? RatedByUserId { get; init; }

    public string? RatedByName { get; init; }

    public DateTime CreatedTime { get; init; }
}
