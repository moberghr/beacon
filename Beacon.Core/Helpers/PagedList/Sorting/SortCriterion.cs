namespace Beacon.Core.Helpers;

public class SortCriterion
{
    public SortCriterion(string sortColumn, SortDirection sortDirection)
    {
        SortColumn = sortColumn;
        SortDirection = sortDirection;
    }

    public string SortColumn { get; init; }

    public SortDirection SortDirection { get; init; }
}