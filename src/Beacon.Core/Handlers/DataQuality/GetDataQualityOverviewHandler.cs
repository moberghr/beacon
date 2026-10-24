using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Models.DataQuality;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Helpers;

namespace Beacon.Core.Handlers.DataQuality.GetDataQualityOverview;

/// <summary>
/// The unpaged overview: every readable data source, read page by page through <see cref="ListDataQualityOverviewHandler"/>
/// (counts and averages are computed in SQL over all of a source's scores; only each source's table list is cut to its
/// lowest-scoring tables) up to <see cref="ListRequest.LegacyUnpagedCap"/> sources.
/// </summary>
internal sealed class GetDataQualityOverviewHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess,
    LegacyListTruncation? truncation = null,
    ILogger<GetDataQualityOverviewHandler>? logger = null) : IRequestHandler<GetDataQualityOverviewQuery, List<DataQualityOverviewData>>
{
    public async Task<List<DataQualityOverviewData>> Handle(GetDataQualityOverviewQuery request, CancellationToken cancellationToken)
    {
        var paged = new ListDataQualityOverviewHandler(contextFactory, projectAccess);
        var overviews = new List<DataQualityOverviewData>();

        for (var page = 0; overviews.Count < ListRequest.LegacyUnpagedCap; page++)
        {
            var result = await paged.Handle(
                new ListDataQualityOverviewQuery { DataSourceId = request.DataSourceId, Page = page, PageSize = ListRequest.MaxPageSize },
                cancellationToken);

            overviews.AddRange(result.Items);
            if (page + 1 >= result.PageCount)
            {
                return overviews;
            }
        }

        truncation?.Mark();
        logger?.LogWarning("The unpaged data quality overview was cut at {Cap} data sources; callers should use its paged route", ListRequest.LegacyUnpagedCap);

        return overviews;
    }
}

public record GetDataQualityOverviewQuery([property: ProjectScopeExempt("filter only: the list is scope-filtered")] int? DataSourceId = null) : IRequest<List<DataQualityOverviewData>>, IProjectFilteredRequest;
