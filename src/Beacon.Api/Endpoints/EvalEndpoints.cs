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
        // writes/edits golden cases. Gate the whole group with the admin policy (matching MCP management,
        // data-sources, migrations, users) rather than the base authenticated policy (§1.1/§1.4).
        var eval = group.MapGroup("/eval").WithTags("Eval")
            .RequireAuthorization(BeaconApiEndpoints.AdminPolicyName);

        eval.MapPost("/golden/promote", (PromoteSignalToGoldenBody body, IMediator m, CancellationToken ct) =>
                m.Send(new PromoteSignalToGoldenCommand(body.SignalId, body.Notes), ct))
            .WithName("PromoteSignalToGolden");

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

internal sealed record PromoteSignalToGoldenBody(int SignalId, string? Notes);
internal sealed record RecordQueryFeedbackBody(int SignalId, McpUserVerdict Verdict, string? CorrectedSql, string? Note);
internal sealed record RunEvalBody(int? ProjectId);
internal sealed record UpdateGoldenCaseBody(string? Question, string? GoldSql, bool? IsActive, string? Notes);
