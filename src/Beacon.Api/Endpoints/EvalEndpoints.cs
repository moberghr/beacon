using Beacon.Api.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.McpEval;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.Api.Endpoints;

/// <summary>
/// REST surface for the MCP text-to-SQL eval harness (§ Architecture ①). One endpoint = one MediatR
/// handler (§2.1.1); endpoints stay thin — resolve path/body, call <c>mediator.Send</c>, return the
/// result. The run trigger resolves the acting user for the run's audit column via
/// <see cref="IActorUserResolver"/>.
/// </summary>
internal static class EvalEndpoints
{
    public static RouteGroupBuilder MapEvalEndpoints(this RouteGroupBuilder group)
    {
        // The eval harness is admin tuning surface: it executes SQL against live data sources and
        // writes/edits golden cases. Gate the whole group with the admin policy, and an Admin by live roles (a
        // session whose Admin role was removed is refused), rather than the base authenticated policy (§1.1/§1.4).
        var eval = group.MapGroup("/eval").WithTags("Eval")
            .RequireBeaconAdmin();

        // Promotes what the Admin reviewed: 409 when the candidate's rating or SQL (its sqlHash) changed since.
        eval.MapPost("/golden/promote", (PromoteSignalToGoldenBody body, IMediator m, CancellationToken ct) =>
                m.Send(new PromoteSignalToGoldenCommand(body.SignalId, body.ReviewedSqlHash, body.Notes), ct))
            .WithName("PromoteSignalToGolden");

        // The answer says whether the corrected SQL was kept, and why not.
        eval.MapPost("/feedback", (RecordQueryFeedbackBody body, IMediator m, CancellationToken ct) =>
                m.Send(new RecordQueryFeedbackCommand(body.SignalId, body.Verdict, body.CorrectedSql, body.Note), ct))
            .WithName("RecordQueryFeedback");

        eval.MapGet("/golden", (
                [FromQuery] int? projectId,
                [FromQuery] int? dataSourceId,
                IMediator m,
                CancellationToken ct) =>
                m.Send(new GetGoldenCasesQuery { ProjectId = projectId, DataSourceId = dataSourceId }, ct))
            .WithName("GetGoldenCases");

        // Correct verdicts no golden case was promoted from yet; a candidate becomes one only through /golden/promote.
        eval.MapGet("/golden/candidates", ([AsParameters] GetGoldenCandidatesQuery query, IMediator m, CancellationToken ct) =>
                m.Send(query, ct))
            .WithName("GetGoldenCandidates");

        // Checks a project's active golden cases and approved learned patterns against today's grounding checks and lists
        // what they refuse, by id, with the reasons. Changes nothing: the Admin decides per row.
        eval.MapGet("/grounding/recheck", ([FromQuery] int projectId, IMediator m, CancellationToken ct) =>
                m.Send(new RecheckGroundingQuery(projectId), ct))
            .WithName("RecheckGrounding");

        eval.MapPut("/golden/{id:int}", async (int id, UpdateGoldenCaseBody body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new UpdateGoldenCaseCommand
            {
                Id = id,
                Question = body.Question,
                GoldSql = body.GoldSql,
                IsActive = body.IsActive,
                Notes = body.Notes
            }, ct);
            return TypedResults.NoContent();
        }).WithName("UpdateGoldenCase");

        eval.MapPost("/runs", async (
            RunEvalBody body,
            IActorUserResolver actorResolver,
            IMediator m,
            CancellationToken ct) =>
        {
            var userId = await actorResolver.ResolveActorUserIdAsync(ct);
            return await m.Send(new RunEvalCommand(body.ProjectId, userId), ct);
        }).WithName("RunEval");

        eval.MapGet("/runs", (
                [FromQuery] int? projectId,
                [FromQuery] int? take,
                IMediator m,
                CancellationToken ct) =>
                m.Send(new GetEvalRunsQuery { ProjectId = projectId, Take = take }, ct))
            .WithName("GetEvalRuns");

        eval.MapGet("/runs/{runId:int}/results", (int runId, IMediator m, CancellationToken ct) =>
                m.Send(new GetEvalResultsQuery(runId), ct))
            .WithName("GetEvalResults");

        return group;
    }
}

/// <param name="SignalId">The candidate to promote.</param>
/// <param name="ReviewedSqlHash">The candidate's <c>sqlHash</c> from the golden candidates list, as reviewed.</param>
/// <param name="Notes">Notes stored on the golden case.</param>
internal sealed record PromoteSignalToGoldenBody(int SignalId, string ReviewedSqlHash, string? Notes);
internal sealed record RecordQueryFeedbackBody(int SignalId, McpUserVerdict Verdict, string? CorrectedSql, string? Note);
internal sealed record RunEvalBody(int? ProjectId);
internal sealed record UpdateGoldenCaseBody(string? Question, string? GoldSql, bool? IsActive, string? Notes);
