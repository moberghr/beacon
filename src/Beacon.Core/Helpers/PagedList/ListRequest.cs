namespace Beacon.Core.Helpers;

/// <summary>
/// Query parameters every paged list endpoint takes. A list request record inherits this, adds its own
/// filters, and builds its result with <c>query.ToPagedListAsync(request, cancellationToken)</c>.
/// <para>
/// <see cref="Page"/> is <b>zero-based</b>. Both paging values are nullable so a bare
/// <c>GET /beacon/api/…</c> is valid, and both are clamped rather than rejected: a page size of 0 or
/// 100 000 is a client bug or a probe, and neither should fail a list that would otherwise render —
/// but an unbounded page size would let one request materialise a whole table.
/// </para>
/// </summary>
public abstract record ListRequest
{
    public const int DefaultPageSize = 20;

    public const int MaxPageSize = 200;

    /// <summary>
    /// The highest page served: <c>MaxPage * MaxPageSize</c> still fits an <see cref="int"/>, so the row offset every
    /// list computes as <c>PageOrDefault * PageSizeOrDefault</c> never overflows into a negative OFFSET.
    /// </summary>
    public const int MaxPage = int.MaxValue / MaxPageSize;

    /// <summary>
    /// The safety cap of a list that kept its pre-paging route. A cut sets the <c>X-Beacon-Truncated</c> header and logs a
    /// warning; a caller that needs every row uses the paged route of the same list.
    /// </summary>
    public const int LegacyUnpagedCap = 10_000;

    /// <summary>The longest search term a list uses; a longer one is cut to it.</summary>
    public const int MaxSearchLength = 200;

    public int? Page { get; init; }

    public int? PageSize { get; init; }

    /// <summary>
    /// Comma-separated column names, each optionally prefixed with <c>-</c> for descending —
    /// e.g. <c>?sort=-createdTime,name</c>. Names match the list item's properties case-insensitively;
    /// unknown or non-sortable columns are ignored.
    /// </summary>
    public string? Sort { get; init; }

    public int PageOrDefault => Math.Clamp(Page ?? 0, 0, MaxPage);

    public int PageSizeOrDefault => Math.Clamp(PageSize ?? DefaultPageSize, 1, MaxPageSize);

    public static string? ClampSearch(string? search)
    {
        var trimmed = search?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > MaxSearchLength ? trimmed[..MaxSearchLength] : trimmed;
    }

    /// <summary>A method rather than a property, so the query binder never treats it as a parameter.</summary>
    public IReadOnlyList<SortCriterion> SortCriteria() => ParseSort(Sort);

    public static IReadOnlyList<SortCriterion> ParseSort(string? sort) =>
        (sort ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x != "-")
            .Select(x => x.StartsWith('-')
                ? new SortCriterion(x[1..], SortDirection.Descending)
                : new SortCriterion(x, SortDirection.Ascending))
            .ToList();
}
