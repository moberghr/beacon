using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;

namespace Beacon.Core.Handlers.McpEval;

/// <summary>
/// Edits a golden eval case — its question, gold SQL, active flag, or notes. Only the fields supplied on
/// the command are applied; the rest are left untouched. Throws (§9.8) when the case does not exist.
/// </summary>
internal sealed class UpdateGoldenCaseHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<UpdateGoldenCaseCommand>
{
    public async Task Handle(UpdateGoldenCaseCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var evalCase = await context.McpEvalCases
            .Where(x => x.Id == request.Id)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Eval case {request.Id} not found.");

        if (request.Question != null)
        {
            evalCase.Question = request.Question;
        }

        if (request.GoldSql != null)
        {
            evalCase.GoldSql = request.GoldSql;
            // The frozen gold fingerprint no longer matches the edited SQL — clear it so the next run
            // re-derives it from a fresh gold execution.
            evalCase.GoldResultFingerprint = null;
        }

        if (request.IsActive.HasValue)
        {
            evalCase.IsActive = request.IsActive.Value;
        }

        if (request.Notes != null)
        {
            evalCase.Notes = request.Notes;
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}

public record UpdateGoldenCaseCommand : IRequest
{
    public required int Id { get; init; }
    public string? Question { get; init; }
    public string? GoldSql { get; init; }
    public bool? IsActive { get; init; }
    public string? Notes { get; init; }
}
