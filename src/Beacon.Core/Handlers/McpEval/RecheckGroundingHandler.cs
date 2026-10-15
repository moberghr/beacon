using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.McpEval;

/// <summary>
/// Checks the grounding a project's prompts are built from against today's checks, changing nothing: the question and gold
/// SQL of each active golden case, and the example question and example SQL of each approved or auto-approved learned
/// pattern still in use (not superseded). SQL passes <see cref="IGroundingSqlCheck"/> in its data source's dialect, in one
/// batch (each data source and its catalog are read once); a question must not be empty (a golden case's) and must hold no
/// character a reviewer cannot see (<see cref="GroundingText"/>). Every row either check refuses is listed by id with the
/// reasons; nothing is rewritten — the Admin decides per row (deactivate the golden case, set the pattern back to pending).
/// Grounding stored before these checks existed keeps grounding prompts until then. A missing project is 404. Logged with
/// counts and ids only (§1.11).
/// </summary>
internal sealed class RecheckGroundingHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IGroundingSqlCheck groundingSqlCheck,
    ILogger<RecheckGroundingHandler> logger)
    : IRequestHandler<RecheckGroundingQuery, RecheckGroundingResult>
{
    internal const string EmptyQuestionRefusal = "The question is empty.";

    internal const string HiddenQuestionRefusal =
        "The question holds characters that do not show on screen (such as bidirectional overrides, zero-width characters, tag "
        + "characters or variation selectors); a prompt leaves it out.";

    public async Task<RecheckGroundingResult> Handle(RecheckGroundingQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var projectExists = await context.Projects
            .Where(x => x.Id == request.ProjectId)
            .AnyAsync(cancellationToken);
        if (!projectExists)
        {
            throw new BeaconNotFoundException($"Project {request.ProjectId} not found.");
        }

        var cases = await context.McpEvalCases
            .Where(x => x.ProjectId == request.ProjectId)
            .Where(x => x.IsActive)
            .OrderBy(x => x.Id)
            .Select(x =>
                new
                {
                    x.Id,
                    x.DataSourceId,
                    x.Question,
                    x.GoldSql,
                    x.PromotedByUserId
                })
            .ToListAsync(cancellationToken);

        var patterns = await context.McpLearnedPatterns
            .Where(x => x.ProjectId == request.ProjectId)
            .Where(x => x.Status == McpPatternStatus.Approved || x.Status == McpPatternStatus.AutoApproved)
            .Where(x => x.SupersededAt == null)
            .OrderBy(x => x.Id)
            .Select(x =>
                new
                {
                    x.Id,
                    x.DataSourceId,
                    x.Status,
                    x.PatternContent,
                    x.ExampleQuestion,
                    x.ExampleSql
                })
            .ToListAsync(cancellationToken);

        var check = groundingSqlCheck.StartBatch();
        var refused = new List<GroundingRecheckItem>();
        foreach (var goldenCase in cases)
        {
            var questionRefusal = string.IsNullOrWhiteSpace(goldenCase.Question) ? EmptyQuestionRefusal : QuestionRefusal(goldenCase.Question);
            var sqlRefusal = (await check.CheckAsync(goldenCase.DataSourceId, goldenCase.GoldSql, cancellationToken)).Refusal;
            if (questionRefusal == null && sqlRefusal == null)
            {
                continue;
            }

            refused.Add(new GroundingRecheckItem
            {
                Kind = GroundingRecheckKind.GoldenCase,
                Id = goldenCase.Id,
                DataSourceId = goldenCase.DataSourceId,
                Question = goldenCase.Question,
                Sql = goldenCase.GoldSql,
                QuestionRefusal = questionRefusal,
                SqlRefusal = sqlRefusal,
                PromotedByUserId = goldenCase.PromotedByUserId
            });
        }

        foreach (var pattern in patterns)
        {
            var questionRefusal = QuestionRefusal(pattern.ExampleQuestion);
            var sqlRefusal = string.IsNullOrWhiteSpace(pattern.ExampleSql)
                ? null
                : (await check.CheckAsync(pattern.DataSourceId, pattern.ExampleSql, cancellationToken)).Refusal;
            if (questionRefusal == null && sqlRefusal == null)
            {
                continue;
            }

            refused.Add(new GroundingRecheckItem
            {
                Kind = GroundingRecheckKind.LearnedPattern,
                Id = pattern.Id,
                DataSourceId = pattern.DataSourceId,
                Question = pattern.ExampleQuestion,
                Sql = pattern.ExampleSql,
                QuestionRefusal = questionRefusal,
                SqlRefusal = sqlRefusal,
                PatternStatus = pattern.Status,
                ContentHash = GroundingContentHash.OfPattern(pattern.PatternContent, pattern.ExampleQuestion, pattern.ExampleSql)
            });
        }

        logger.LogInformation(
            "Grounding recheck of project {ProjectId}: {RefusedCount} of {CaseCount} active golden cases and {PatternCount} approved learned patterns refused (golden cases {CaseIds}; patterns {PatternIds})",
            request.ProjectId,
            refused.Count,
            cases.Count,
            patterns.Count,
            string.Join(",", Ids(refused, GroundingRecheckKind.GoldenCase)),
            string.Join(",", Ids(refused, GroundingRecheckKind.LearnedPattern)));

        return new RecheckGroundingResult
        {
            ProjectId = request.ProjectId,
            GoldenCasesChecked = cases.Count,
            PatternsChecked = patterns.Count,
            Refused = refused
        };
    }

    private static string? QuestionRefusal(string? question)
    {
        return GroundingText.HasHiddenCharacters(question) ? HiddenQuestionRefusal : null;
    }

    private static IEnumerable<int> Ids(IEnumerable<GroundingRecheckItem> items, GroundingRecheckKind kind)
    {
        return items
            .Where(x => x.Kind == kind)
            .Select(x => x.Id);
    }
}

/// <param name="ProjectId">The project whose grounding is checked.</param>
[ProjectScopeExempt("admin-gated curation (the eval harness)", Policy = ProjectScopeExemptAttribute.AdminPolicy)]
public record RecheckGroundingQuery(int ProjectId) : IRequest<RecheckGroundingResult>;

public record RecheckGroundingResult
{
    public int ProjectId { get; init; }

    /// <summary>The active golden cases checked.</summary>
    public int GoldenCasesChecked { get; init; }

    /// <summary>The approved and auto-approved learned patterns checked (superseded ones are in no prompt).</summary>
    public int PatternsChecked { get; init; }

    /// <summary>The golden cases and patterns today's checks refuse, golden cases first, each in id order.</summary>
    public List<GroundingRecheckItem> Refused { get; init; } = [];
}

/// <summary>What a grounding recheck row is.</summary>
public enum GroundingRecheckKind
{
    /// <summary>An active golden case: deactivate it (<c>PUT /eval/golden/{id}</c>) or edit it.</summary>
    GoldenCase = 0,

    /// <summary>An approved or auto-approved learned pattern: set it back to pending with its <see cref="GroundingRecheckItem.ContentHash"/>.</summary>
    LearnedPattern = 1
}

/// <summary>A golden case or learned pattern the grounding recheck refuses, with the reasons; the stored text, unchanged.</summary>
public record GroundingRecheckItem
{
    public GroundingRecheckKind Kind { get; init; }

    public int Id { get; init; }

    public int DataSourceId { get; init; }

    /// <summary>The golden case's question, or the pattern's example question, as stored.</summary>
    public string? Question { get; init; }

    /// <summary>The golden case's gold SQL, or the pattern's example SQL, as stored.</summary>
    public string? Sql { get; init; }

    /// <summary>Why the question is refused; null when it passes.</summary>
    public string? QuestionRefusal { get; init; }

    /// <summary>Why the SQL is refused (the grounding SQL check's reason); null when it passes.</summary>
    public string? SqlRefusal { get; init; }

    /// <summary>A golden case's promoting user; null for a pattern, and for a case promoted before Beacon recorded it.</summary>
    public int? PromotedByUserId { get; init; }

    /// <summary>A pattern's status (Approved or AutoApproved); null for a golden case.</summary>
    public McpPatternStatus? PatternStatus { get; init; }

    /// <summary>
    /// A pattern's content hash (as the learned-pattern list hands it out), which a status change sends back; null for a
    /// golden case.
    /// </summary>
    public string? ContentHash { get; init; }
}
