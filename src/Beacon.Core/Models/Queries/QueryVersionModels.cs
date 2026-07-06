using Beacon.Core.Data.Enums;

namespace Beacon.Core.Models.Queries;

public class QueryVersionSummary
{
    public int Id { get; set; }
    public int VersionNumber { get; set; }
    public string? Label { get; set; }
    public QueryVersionStatus Status { get; set; }
    public string Name { get; set; } = null!;
    public DateTime CreatedTime { get; set; }
    public string? CreatedByUserId { get; set; }
    public string? ChangeSource { get; set; }
    public string? ChangeReason { get; set; }
    public int StepCount { get; set; }
}

public class QueryVersionDetail
{
    public int Id { get; set; }
    public int VersionNumber { get; set; }
    public string? Label { get; set; }
    public QueryVersionStatus Status { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public string? FinalQuery { get; set; }
    public DateTime CreatedTime { get; set; }
    public string? CreatedByUserId { get; set; }
    public string? ChangeSource { get; set; }
    public string? ChangeReason { get; set; }
    public List<QueryStepSnapshot> Steps { get; set; } = [];
}

public class QueryVersionDiff
{
    public QueryVersionDetail VersionA { get; set; } = null!;
    public QueryVersionDetail VersionB { get; set; } = null!;
    public bool NameChanged { get; set; }
    public bool DescriptionChanged { get; set; }
    public bool FinalQueryChanged { get; set; }
    public List<StepDiff> StepDiffs { get; set; } = [];
}

public class StepDiff
{
    public int StepOrder { get; set; }
    public StepDiffType DiffType { get; set; }
    public QueryStepSnapshot? StepA { get; set; }
    public QueryStepSnapshot? StepB { get; set; }
}

public enum StepDiffType
{
    Unchanged,
    Modified,
    Added,
    Removed
}
