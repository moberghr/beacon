using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;

namespace Beacon.Core.Handlers.McpEval;

/// <summary>
/// Promotes an <see cref="McpQuerySignal"/> into a golden <see cref="McpEvalCase"/>. The gold SQL is
/// the signal's corrected SQL when present (the human-verified fix), otherwise its generated SQL.
/// Throws (§9.8) when the signal is missing, carries no SQL, or is not scoped to a project + data source.
/// </summary>
internal sealed class PromoteSignalToGoldenHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<PromoteSignalToGoldenCommand, PromoteSignalToGoldenResult>
{
    public async Task<PromoteSignalToGoldenResult> Handle(PromoteSignalToGoldenCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var signal = await context.McpQuerySignals
            .Where(x => x.Id == request.SignalId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Query signal {request.SignalId} not found.");

        var goldSql = signal.CorrectedSql ?? signal.GeneratedSql;
        if (string.IsNullOrWhiteSpace(goldSql))
        {
            throw new InvalidOperationException($"Query signal {request.SignalId} has no SQL to promote.");
        }

        if (signal.ProjectId is not { } projectId)
        {
            throw new InvalidOperationException($"Query signal {request.SignalId} is not associated with a project.");
        }

        if (signal.DataSourceId is not { } dataSourceId)
        {
            throw new InvalidOperationException($"Query signal {request.SignalId} is not associated with a data source.");
        }

        var evalCase = new McpEvalCase
        {
            ProjectId = projectId,
            DataSourceId = dataSourceId,
            Question = signal.Question,
            GoldSql = goldSql,
            SourceSignalId = signal.Id,
            Notes = request.Notes,
            IsActive = true
        };

        context.McpEvalCases.Add(evalCase);
        await context.SaveChangesAsync(cancellationToken);

        return new PromoteSignalToGoldenResult(evalCase.Id);
    }
}

public record PromoteSignalToGoldenCommand(int SignalId, string? Notes = null) : IRequest<PromoteSignalToGoldenResult>;

public record PromoteSignalToGoldenResult(int EvalCaseId);
