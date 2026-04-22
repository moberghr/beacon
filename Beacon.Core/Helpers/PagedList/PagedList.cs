namespace Beacon.Core.Helpers;

public class PagedList<T>
{
    public PagedList(List<T> items)
    {
        Items = items;
    }

    public PagedList(List<T> items, int totalCount)
        : this(items)
    {
        TotalCount = totalCount;
    }

    public int? TotalCount { get; set; }

    public List<T> Items { get; set; }
}