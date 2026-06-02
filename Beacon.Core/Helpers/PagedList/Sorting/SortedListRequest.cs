namespace Beacon.Core.Helpers;

public class SortedListRequest : BaseListRequest
{
    public List<SortCriterion> SortCriteria { get; set; } = new();
}