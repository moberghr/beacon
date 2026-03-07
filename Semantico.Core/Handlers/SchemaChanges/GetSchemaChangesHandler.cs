using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Handlers.SchemaChanges;

internal sealed class GetSchemaChangesHandler(SemanticoContext context)
    : IRequestHandler<GetSchemaChangesQuery, GetSchemaChangesResult>
{
    public async Task<GetSchemaChangesResult> Handle(
        GetSchemaChangesQuery request,
        CancellationToken cancellationToken)
    {
        var rows = await context.SchemaChanges
            .OrderByDescending(sc => sc.DetectedAt)
            .Select(sc => new
            {
                DataSourceName = sc.DataSource.Name,
                sc.SchemaName,
                sc.TableName,
                sc.ColumnName,
                sc.ChangeType,
                sc.OldValue,
                sc.NewValue,
                sc.Description,
                sc.DetectedAt
            })
            .ToListAsync(cancellationToken);

        var entries = rows.Select(r => new SchemaChangeEntry(
            r.DataSourceName,
            r.SchemaName,
            r.TableName,
            r.ColumnName,
            r.ChangeType,
            r.OldValue,
            r.NewValue,
            r.Description,
            r.DetectedAt)).ToList();

        return new GetSchemaChangesResult(entries);
    }
}

public record GetSchemaChangesQuery : IRequest<GetSchemaChangesResult>;

public record GetSchemaChangesResult(List<SchemaChangeEntry> Entries);

public record SchemaChangeEntry(
    string DataSourceName,
    string SchemaName,
    string TableName,
    string? ColumnName,
    SchemaChangeType ChangeType,
    string? OldValue,
    string? NewValue,
    string? Notes,
    DateTime DetectedAt);
