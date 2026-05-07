using Beacon.Core.Helpers;

namespace Beacon.Core.Models.QueryExecutionHistory;

public class QueryExecutionHistoryListData : IPagedListResponse<QueryExecutionHistoryData>
{
    public List<QueryExecutionHistoryData> Data { get; set; }

    public int? TotalCount { get; set; }
    public int? LastQueryExecutionHistoryId { get; init; }
}