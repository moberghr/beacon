using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Handlers.DataCatalog;

internal sealed class GetDataCatalogHandler(SemanticoContext context)
    : IRequestHandler<GetDataCatalogQuery, GetDataCatalogResult>
{
    public async Task<GetDataCatalogResult> Handle(
        GetDataCatalogQuery request,
        CancellationToken cancellationToken)
    {
        var entries = await (
            from dm in context.DatabaseMetadata
            join ds in context.DataSources on dm.DataSourceId equals ds.Id
            where (ds.DataSourceType == DataSourceType.Database || ds.DataSourceType == DataSourceType.Api)
                && dm.ArchivedTime == null && ds.ArchivedTime == null
            select new
            {
                ds.Name,
                dm.SchemaName,
                dm.TableName,
                dm.TableDescription,
                dm.DataSourceId,
                ColumnCount = dm.Columns.Count,
                DataSourceType = ds.DataSourceType
            }
        ).ToListAsync(cancellationToken);

        // Batch load quality scores
        var qualityScores = await context.DataQualityScores
            .Select(q => new { q.DataSourceId, q.SchemaName, q.TableName, q.Score })
            .ToListAsync(cancellationToken);

        var qualityLookup = qualityScores
            .ToDictionary(q => (q.DataSourceId, q.SchemaName, q.TableName), q => (double?)q.Score);

        // Batch load code reference counts
        var codeRefCounts = await context.CodeReferences
            .Where(c => c.SchemaName != null && c.TableName != null)
            .GroupBy(c => new { c.SchemaName, c.TableName })
            .Select(g => new { g.Key.SchemaName, g.Key.TableName, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var codeRefLookup = codeRefCounts
            .ToDictionary(c => (c.SchemaName!, c.TableName!), c => c.Count);

        var catalog = entries.Select(e => new DataCatalogEntry(
            e.Name,
            e.SchemaName,
            e.TableName,
            e.TableDescription,
            e.ColumnCount,
            qualityLookup.GetValueOrDefault((e.DataSourceId, e.SchemaName, e.TableName)),
            codeRefLookup.GetValueOrDefault((e.SchemaName, e.TableName)),
            e.DataSourceType
        )).ToList();

        return new GetDataCatalogResult(catalog);
    }
}

public record GetDataCatalogQuery : IRequest<GetDataCatalogResult>;

public record GetDataCatalogResult(List<DataCatalogEntry> Entries);

public record DataCatalogEntry(
    string DataSourceName,
    string SchemaName,
    string TableName,
    string? Description,
    int ColumnCount,
    double? QualityScore,
    int CodeReferenceCount,
    DataSourceType DataSourceType = DataSourceType.Database);
