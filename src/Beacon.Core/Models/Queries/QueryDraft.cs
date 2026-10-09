namespace Beacon.Core.Models.Queries;

/// <summary>
/// The query editor's unsaved steps and final query. A preview runs these instead of the stored query,
/// so trying an edit never saves it and never writes a version.
/// </summary>
public class QueryDraft
{
    public List<QueryStepData> Steps { get; set; } = new();

    public string? FinalQuery { get; set; }
}
