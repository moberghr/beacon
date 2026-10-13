using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Authorization;
using Beacon.Core.Models;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.McpLearning;

/// <summary>
/// Sets a learned pattern's review status — <see cref="McpPatternStatus.Approved"/>, <see cref="McpPatternStatus.Rejected"/>
/// or back to <see cref="McpPatternStatus.Pending"/>; any other status is refused (§9.8), since the others are the
/// aggregation job's — and records the reviewer, the current caller (403 without a Beacon user: who decided is recorded).
/// Reviewing needs <see cref="ProjectAction.Curate"/> in the pattern's project. A decision applies only to the content the
/// reviewer saw: the command carries the hash the pattern list showed (<see cref="LearnedPatternEntry.ContentHash"/>), and
/// the status is written only while the pattern still holds that content — in one conditional update, so a refresh between
/// the read and the write is never decided unseen (409 otherwise). Approving a pattern that carries example SQL checks that
/// SQL again (<see cref="IGroundingSqlCheck"/>, in the pattern's data source's dialect) and stores it without its comments;
/// SQL the check refuses refuses the approval (§9.8), and nothing is changed.
/// </summary>
internal sealed class UpdatePatternStatusHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IGroundingSqlCheck groundingSqlCheck,
    IBeaconActorAccessor actorAccessor,
    ILogger<UpdatePatternStatusHandler> logger)
    : IRequestHandler<UpdatePatternStatusCommand>
{
    internal const string ChangedSinceReviewedMessage =
        "The pattern changed since it was reviewed: reload the learned patterns and review it again.";

    internal const string UnidentifiedReviewerMessage = "Reviewing a learned pattern needs a signed-in Beacon user: who decided is recorded.";

    private static readonly IReadOnlySet<McpPatternStatus> ReviewStatuses = new HashSet<McpPatternStatus>
    {
        McpPatternStatus.Approved,
        McpPatternStatus.Rejected,
        McpPatternStatus.Pending
    };

    public async Task Handle(UpdatePatternStatusCommand request, CancellationToken cancellationToken)
    {
        if (!ReviewStatuses.Contains(request.NewStatus))
        {
            throw new InvalidOperationException("A pattern can only be approved, rejected or set back to pending.");
        }

        var reviewer = await actorAccessor.GetCurrentAsync(cancellationToken);
        if (reviewer.BeaconUserId == null)
        {
            throw AccessRefusal.Of(logger, reviewer, "learned pattern", request.PatternId, UnidentifiedReviewerMessage);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var pattern = await context.McpLearnedPatterns
            .Where(x => x.Id == request.PatternId)
            .Select(x =>
                new
                {
                    x.DataSourceId,
                    x.PatternContent,
                    x.ExampleQuestion,
                    x.ExampleSql
                })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Pattern {request.PatternId} not found");

        var content = GroundingContentHash.OfPattern(pattern.PatternContent, pattern.ExampleQuestion, pattern.ExampleSql);
        if (!GroundingContentHash.Matches(request.ReviewedContentHash, content))
        {
            throw new BeaconConflictException(ChangedSinceReviewedMessage);
        }

        var exampleSql = pattern.ExampleSql;
        if (request.NewStatus == McpPatternStatus.Approved && !string.IsNullOrWhiteSpace(pattern.ExampleSql))
        {
            var check = await groundingSqlCheck.CheckAsync(pattern.DataSourceId, pattern.ExampleSql, cancellationToken);
            if (!check.IsAllowed)
            {
                throw new InvalidOperationException($"Pattern {request.PatternId} was not approved: its example SQL does not pass the SQL check. {check.Refusal}");
            }

            exampleSql = check.Sql;
        }

        var reviewedAt = DateTime.UtcNow;
        var written = await context.McpLearnedPatterns
            .Where(x => x.Id == request.PatternId)
            .Where(x => x.PatternContent == pattern.PatternContent)
            .Where(x => x.ExampleQuestion == pattern.ExampleQuestion)
            .Where(x => x.ExampleSql == pattern.ExampleSql)
            .ExecuteUpdateAsync(
                x => x
                    .SetProperty(y => y.Status, request.NewStatus)
                    .SetProperty(y => y.ExampleSql, exampleSql)
                    .SetProperty(y => y.ReviewedByUserId, reviewer.BeaconUserId)
                    .SetProperty(y => y.ReviewedAt, reviewedAt),
                cancellationToken);
        if (written == 0)
        {
            throw new BeaconConflictException(ChangedSinceReviewedMessage);
        }
    }
}

public record UpdatePatternStatusCommand : IRequest, IProjectAuthorizedRequest
{
    public required int PatternId { get; init; }
    public required McpPatternStatus NewStatus { get; init; }

    /// <summary>
    /// The <see cref="LearnedPatternEntry.ContentHash"/> of the pattern as the reviewer saw it; a pattern whose content hashes
    /// otherwise now is not changed (409).
    /// </summary>
    public required string ReviewedContentHash { get; init; }

    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.LearnedPattern(PatternId), ProjectAction.Curate)
    ];
}
