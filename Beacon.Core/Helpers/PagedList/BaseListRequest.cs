namespace Beacon.Core.Helpers;

public class BaseListRequest
{
    private int? _page;
    private int? _pageSize;

    public int? Page
    {
        get => _page;
        set => _page = value;
    }

    public int? PageSize
    {
        get => _pageSize;
        set => _pageSize = value;
    }

    public int PageOrDefault => _page ?? 0;

    public int PageSizeOrDefault => _pageSize ?? 20;
}
