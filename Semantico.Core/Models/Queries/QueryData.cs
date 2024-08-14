namespace Semantico.Core.Models.Queries
{
    public class QueryData
    {
        public int? QueryId { get; init; }

        public required string SqlValue { get; init; }

        public required int ProjectId { get; init; }

        public List<QueryParameterData> Parameters { get; init; } = new();
    }
}
