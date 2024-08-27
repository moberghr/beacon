namespace Semantico.Core.Models.QueryExecutionHistory
{
    public class QueryExecutionHistoryListData
    {
        public required List<QueryExecutionHistoryData> QueryExecutionHistory { get; set; }

        public int? LastQueryExecutionHistoryId { get; init; }
    }
}