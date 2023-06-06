namespace Semantico.Api.Adapters;

public class MessageRequest
{
    public required string QueryResults { get; init; }

    public required int TotalRecords { get; init; }

    public required string ProjectName { get; init; }

    public required string SqlQuery { get; init; }

    public string Recipient { get; set; } = string.Empty;
}

