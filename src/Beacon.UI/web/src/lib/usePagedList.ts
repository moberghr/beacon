import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import {
  DEFAULT_PAGE_SIZE,
  fetchPagedList,
  parseSortParam,
  sortToParam,
  toggleSort,
  type ListParams,
  type PagedList,
  type SortState,
} from "./paging";

type Filters = Record<string, string>;

export interface UsePagedListOptions<F extends Filters> {
  /** Root query key; mutations that invalidate it refresh the grid. */
  queryKey: readonly unknown[];
  /** Endpoint path, e.g. `/beacon/api/tasks`. */
  path: string;
  pageSize?: number;
  /** Order shown when the URL names none; the server applies its own default when this is null. */
  defaultSort?: SortState | null;
  /** Filter names and their defaults ('' means no filter). Sent as query parameters of the same name. */
  filters?: F;
  /** Fixed parameters that are not user state, e.g. `{ subscriptionId: 8 }`. */
  fixedParams?: ListParams;
  /** Translates UI filter values into API parameters, when they differ (a `status` tab → `resolved`). */
  mapFilters?: (filters: F) => ListParams;
  /** Namespaces the URL keys when several grids share a page: `runsPage`, `runsSort`, `runsStatus`. */
  urlPrefix?: string;
  enabled?: boolean;
  /** Polling for live views, e.g. 30 000 ms for Control Tower. */
  refetchInterval?: number;
}

/**
 * Server-side list state for a grid: page, sort and filters live in the URL, so back/forward and shared
 * links restore the same view. The URL's `page` is one-based for people (`?page=2` is the second page);
 * the API is zero-based, and this hook is the only place that converts.
 */
export function usePagedList<T, F extends Filters = Record<string, never>>(
  options: UsePagedListOptions<F>,
) {
  const {
    queryKey,
    path,
    pageSize = DEFAULT_PAGE_SIZE,
    defaultSort = null,
    filters: filterDefaults = {} as F,
    fixedParams = {},
    mapFilters,
    urlPrefix = "",
    enabled = true,
    refetchInterval,
  } = options;
  const [searchParams, setSearchParams] = useSearchParams();
  const urlKey = useCallback(
    (name: string) =>
      urlPrefix ? `${urlPrefix}${name[0].toUpperCase()}${name.slice(1)}` : name,
    [urlPrefix],
  );

  const page = Math.max(0, (Number(searchParams.get(urlKey("page"))) || 1) - 1);
  const sort = searchParams.has(urlKey("sort"))
    ? parseSortParam(searchParams.get(urlKey("sort")))
    : defaultSort;
  const filterKey = JSON.stringify(filterDefaults);
  const filters = useMemo(
    () =>
      Object.fromEntries(
        Object.entries(filterDefaults).map(([name, fallback]) => [
          name,
          searchParams.get(urlKey(name)) ?? fallback,
        ]),
      ) as F,
    // filterDefaults is compared by value (filterKey): callers pass an object literal.
    [searchParams, urlKey, filterKey],
  );

  const update = useCallback(
    (mutate: (next: URLSearchParams) => void, replace: boolean) =>
      setSearchParams(
        (previous) => {
          const next = new URLSearchParams(previous);
          mutate(next);
          return next;
        },
        { replace },
      ),
    [setSearchParams],
  );

  const setPage = useCallback(
    (next: number) =>
      update((params) => {
        if (next <= 0) params.delete(urlKey("page"));
        else params.set(urlKey("page"), String(next + 1));
      }, false),
    [update, urlKey],
  );

  const setSort = useCallback(
    (next: SortState | null) =>
      update((params) => {
        const value = sortToParam(next);
        if (value === sortToParam(defaultSort)) params.delete(urlKey("sort"));
        else params.set(urlKey("sort"), value ?? "");
        params.delete(urlKey("page"));
      }, false),
    [update, urlKey, defaultSort],
  );

  /** Filters replace history (typing must not fill the back stack) and return to the first page. */
  const setFilter = useCallback(
    (name: keyof F & string, value: string) =>
      update((params) => {
        if (value === (filterDefaults[name] ?? "")) params.delete(urlKey(name));
        else params.set(urlKey(name), value);
        params.delete(urlKey("page"));
      }, true),
    [update, urlKey, filterKey],
  );

  /** Every filter back to its default in one history entry, first page, sort kept. */
  const resetFilters = useCallback(
    () =>
      update((params) => {
        for (const name of Object.keys(filterDefaults))
          params.delete(urlKey(name));
        params.delete(urlKey("page"));
      }, true),
    // filterDefaults is compared by value (filterKey).
    [update, urlKey, filterKey],
  );

  const apiParams: ListParams = {
    ...fixedParams,
    ...(mapFilters ? mapFilters(filters) : filters),
    page,
    pageSize,
    sort: sortToParam(sort),
  };
  const query = useQuery<PagedList<T>>({
    queryKey: [...queryKey, "paged", apiParams],
    queryFn: () => fetchPagedList<T>(path, apiParams),
    placeholderData: keepPreviousData,
    enabled,
    refetchInterval,
  });

  // A page past the end (last row of the last page deleted, stale link): step back to the last real page.
  const pageCount = query.data?.pageCount;
  useEffect(() => {
    if (pageCount !== undefined && page > 0 && page >= pageCount) {
      setPage(Math.max(0, pageCount - 1));
    }
  }, [page, pageCount, setPage]);

  return {
    ...query,
    items: query.data?.items ?? [],
    page,
    pageSize,
    sort,
    filters,
    setPage,
    setSort,
    setFilter,
    resetFilters,
    /** Spread onto `<DataTable>`: sortable headers and the pager footer. */
    tableProps: {
      sort,
      onSortChange: (column: string) => setSort(toggleSort(sort, column)),
      paging: {
        page,
        pageSize,
        pageCount: query.data?.pageCount ?? 0,
        totalCount: query.data?.totalCount ?? 0,
        onPageChange: setPage,
      },
      loading: query.isFetching,
    },
  };
}

/**
 * A search box bound to a URL filter: typing updates the input at once and the filter (and so the
 * request) after `delayMs` of quiet. Returns `[inputValue, setInputValue]`.
 */
export function useSearchFilter(
  current: string,
  apply: (value: string) => void,
  delayMs = 300,
) {
  const [value, setValue] = useState(current);
  // Callers pass an inline arrow; a ref keeps unrelated re-renders from restarting the debounce.
  const applyRef = useRef(apply);
  applyRef.current = apply;

  // The filter changed from outside the box (reset, back/forward): show it.
  useEffect(() => {
    setValue((previous) => (previous.trim() === current ? previous : current));
  }, [current]);

  useEffect(() => {
    const trimmed = value.trim();
    if (trimmed === current) return;
    const handle = window.setTimeout(() => applyRef.current(trimmed), delayMs);
    return () => window.clearTimeout(handle);
  }, [value, current, delayMs]);

  return [value, setValue] as const;
}
