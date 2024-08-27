using Semantico.Core.Data.Enums;

namespace Semantico.Core.Models.QueryExecutionHistory
{
    public class QueryExecutionHistoryData
    {
        public required int QueryExecutionHistoryId { get; set; }

        public required string Recipient { get; set; }

        public required NotificationType NotificationType { get; set; }

        public required int ResultCount { get; set; }
    }
}
