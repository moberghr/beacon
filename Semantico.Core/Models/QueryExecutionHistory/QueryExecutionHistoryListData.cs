using Semantico.Core.Helpers;

namespace Semantico.Core.Models.QueryExecutionHistory;

public class QueryExecutionHistoryListData: IPagedListResponse<QueryExecutionHistoryData>
{
    public List<QueryExecutionHistoryData> Data { get; set; }

    public int? TotalCount { get; set; }
    public int? LastQueryExecutionHistoryId { get; init; }
}