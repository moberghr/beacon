namespace Beacon.Core.Helpers;

/// <summary>
/// One page of a list — the single wire shape of every paged list endpoint:
/// <c>{ items, totalCount, pageCount }</c>. <see cref="TotalCount"/> counts the whole filtered set,
/// not the page.
/// </summary>
public sealed class PagedList<T>
{
    public required List<T> Items { get; init; }

    public required int TotalCount { get; init; }

    public required int PageCount { get; init; }

    public static PagedList<T> Create(List<T> items, int totalCount, int pageSize) =>
        new()
        {
            Items = items,
            TotalCount = totalCount,
            PageCount = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize)
        };

    /// <summary>The same page with its items mapped, for results enriched after the paged query.</summary>
    public PagedList<TResult> Map<TResult>(Func<T, TResult> map) =>
        new()
        {
            Items = Items
                .Select(map)
                .ToList(),
            TotalCount = TotalCount,
            PageCount = PageCount
        };
}
