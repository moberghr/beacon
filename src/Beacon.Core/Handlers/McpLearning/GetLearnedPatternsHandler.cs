using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;

namespace Beacon.Core.Handlers.McpLearning;

internal sealed class GetLearnedPatternsHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetLearnedPatternsQuery, PagedList<LearnedPatternEntry>>
{
    public async Task<PagedList<LearnedPatternEntry>> Handle(GetLearnedPatternsQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = context.McpLearnedPatterns.AsQueryable();

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

        return await query
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
                LastRefreshedAt = p.LastRefreshedAt
            })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "-confidence,-signalCount");
    }
}

/// <summary>Most confident first (then most signals) unless <c>sort</c> says otherwise.</summary>
public record GetLearnedPatternsQuery : ListRequest, IRequest<PagedList<LearnedPatternEntry>>
{
    public int? ProjectId { get; init; }
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
}
