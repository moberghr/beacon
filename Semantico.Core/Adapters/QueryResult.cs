using Semantico.Core.Data.Enums;

namespace Semantico.Core.Adapters;

public class QueryResult
{
    public required string QueryResults { get; init; }

    public required int TotalRecords { get; init; }

    public required string ProjectName { get; init; }

    public required string SqlQuery { get; init; }
}

public class RecipientQueryResult
{
    public required string SubscriptionName { get; init; }

    public required string RecipientDestination { get; init; }

    public required NotificationType RecipientNotificationType { get; init; }

    public required QueryResult QueryResult { get; init; }

    public QueryResultFile? QueryResultFile { get; init; }
}

public class QueryResultFile
{
    public required byte[] Data { get; init; }

    public required string Name { get; init; }

    public required string ContentType { get; init; }
}