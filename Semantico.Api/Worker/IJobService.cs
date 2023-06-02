namespace Semantico.Api.Worker;

public interface IJobService
{
    Task ExecuteQuery(int queryId);
}

public class MessageRequest
{
    public required string QueryResults { get; init; }

    public required int TotalRecords { get; init; }

    public required string ProjectName { get; init; }
}