import { fetchJson } from "./api";

/**
 * One page of a server list — the wire shape every paged `/beacon/api` list endpoint returns.
 * `totalCount` counts the whole filtered set, not the page.
 */
export interface PagedList<T> {
  items: T[];
  totalCount: number;
  pageCount: number;
}

export const DEFAULT_PAGE_SIZE = 20;

export type SortDirection = "asc" | "desc";

export interface SortState {
  /** A property name of the list item, as the server sorts by (case-insensitive). */
  column: string;
  direction: SortDirection;
}

/** `{ column: 'createdTime', direction: 'desc' }` → `-createdTime`, the server's `sort` syntax. */
export function sortToParam(
  sort: SortState | null | undefined,
): string | undefined {
  if (!sort) return undefined;
  return sort.direction === "desc" ? `-${sort.column}` : sort.column;
}

export function parseSortParam(
  value: string | null | undefined,
): SortState | null {
  const first = value?.split(",")[0]?.trim();
  if (!first || first === "-") return null;
  return first.startsWith("-")
    ? { column: first.slice(1), direction: "desc" }
    : { column: first, direction: "asc" };
}

/** Clicking a header: a new column sorts ascending, the same column flips direction. */
export function toggleSort(
  current: SortState | null,
  column: string,
): SortState {
  if (current?.column === column) {
    return { column, direction: current.direction === "asc" ? "desc" : "asc" };
  }
  return { column, direction: "asc" };
}

export type ListParams = Record<
  string,
  string | number | boolean | null | undefined
>;

/** Query string for a list request; empty, null and undefined values are left out. */
export function listQueryString(params: ListParams): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value === undefined || value === null || value === "") continue;
    search.set(key, String(value));
  }
  const text = search.toString();
  return text ? `?${text}` : "";
}

export function fetchPagedList<T>(
  path: string,
  params: ListParams,
): Promise<PagedList<T>> {
  return fetchJson<PagedList<T>>(`${path}${listQueryString(params)}`);
}
