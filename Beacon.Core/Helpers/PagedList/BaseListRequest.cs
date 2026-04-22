namespace Beacon.Core.Helpers;

public class BaseListRequest
{
    public int Page { get; set; }

    public int PageSize { get; set; } = 20;
}