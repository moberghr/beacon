namespace Beacon.Core.Helpers;

public interface IPagedListResponse<T>
{
    public List<T> Data { get; set; }

    public int? TotalCount { get; set; }
}
