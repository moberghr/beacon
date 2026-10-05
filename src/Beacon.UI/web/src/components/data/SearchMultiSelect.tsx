import { useEffect, useState, type ReactNode } from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { X } from "lucide-react";
import { Input } from "@/components/beacon";
import { cn } from "@/lib/cn";
import {
  DEFAULT_PAGE_SIZE,
  fetchPagedList,
  type ListParams,
} from "@/lib/paging";

export interface SearchMultiSelectProps<T> {
  /** List endpoint that accepts `search`, e.g. `/beacon/api/recipients`. */
  path: string;
  params?: ListParams;
  queryKey: readonly unknown[];
  /** The chosen items themselves (not ids), so chips keep their labels across searches. */
  selected: T[];
  onChange: (selected: T[]) => void;
  getId: (item: T) => number;
  getLabel: (item: T) => string;
  /** Row content next to the checkbox; defaults to the label. */
  renderItem?: (item: T) => ReactNode;
  /** Plural noun for empty states and the search placeholder. */
  noun: string;
  /** Shown when there is nothing to pick at all (no search term, zero rows). */
  emptyContent?: ReactNode;
  /** Items that cannot be picked (already attached, say), shown checked and disabled. */
  lockedIds?: number[];
  hasError?: boolean;
  className?: string;
}

/**
 * Multi-select over a paged list endpoint: a search box, the first page of matches as checkboxes, and
 * the current selection as removable chips. Never loads the whole list.
 */
export function SearchMultiSelect<T>({
  path,
  params,
  queryKey,
  selected,
  onChange,
  getId,
  getLabel,
  renderItem,
  noun,
  emptyContent,
  lockedIds = [],
  hasError,
  className,
}: SearchMultiSelectProps<T>) {
  const [term, setTerm] = useState("");
  const [debounced, setDebounced] = useState("");

  useEffect(() => {
    const handle = window.setTimeout(() => setDebounced(term.trim()), 200);
    return () => window.clearTimeout(handle);
  }, [term]);

  const matches = useQuery({
    queryKey: [...queryKey, "multi-select", params ?? {}, debounced],
    queryFn: () =>
      fetchPagedList<T>(path, {
        ...params,
        search: debounced || undefined,
        pageSize: DEFAULT_PAGE_SIZE,
      }),
    placeholderData: keepPreviousData,
  });

  const items = matches.data?.items ?? [];
  const totalCount = matches.data?.totalCount ?? 0;
  const selectedIds = new Set(selected.map(getId));
  const locked = new Set(lockedIds);

  const toggle = (item: T) => {
    const id = getId(item);
    onChange(
      selectedIds.has(id)
        ? selected.filter((x) => getId(x) !== id)
        : [...selected, item],
    );
  };

  return (
    <div className={cn("flex flex-col gap-2", className)}>
      {selected.length > 0 && (
        <div className="flex flex-wrap gap-1.5" aria-label={`Selected ${noun}`}>
          {selected.map((item) => (
            <span
              key={getId(item)}
              className="inline-flex items-center gap-1 rounded-xs border border-border-strong bg-surface-2 px-1.5 py-0.5 text-xs"
            >
              {getLabel(item)}
              <button
                type="button"
                aria-label={`Remove ${getLabel(item)}`}
                onClick={() => toggle(item)}
                className="text-text-muted hover:text-text"
              >
                <X className="size-3" />
              </button>
            </span>
          ))}
        </div>
      )}

      <Input
        type="search"
        value={term}
        placeholder={`Search ${noun}…`}
        aria-label={`Search ${noun}`}
        aria-invalid={hasError}
        onChange={(e) => setTerm(e.target.value)}
      />

      <div
        className={cn(
          "border border-border rounded-sm max-h-72 overflow-auto",
          hasError && "border-crit",
        )}
      >
        {matches.isLoading && (
          <div className="text-text-muted p-3 text-sm">Loading {noun}…</div>
        )}
        {!matches.isLoading && items.length === 0 && (
          <div className="text-text-muted p-3 text-sm">
            {debounced
              ? `No ${noun} match "${debounced}".`
              : (emptyContent ?? `No ${noun} yet.`)}
          </div>
        )}
        {items.map((item) => {
          const id = getId(item);
          const isLocked = locked.has(id);
          return (
            <label
              key={id}
              className={cn(
                "flex items-center gap-3 px-3 py-2 border-b border-border last:border-b-0 text-sm",
                isLocked ? "opacity-60" : "cursor-pointer hover:bg-surface-2",
              )}
            >
              <input
                type="checkbox"
                checked={isLocked || selectedIds.has(id)}
                disabled={isLocked}
                onChange={() => toggle(item)}
                aria-label={getLabel(item)}
              />
              <div className="flex-1 min-w-0">
                {renderItem ? renderItem(item) : getLabel(item)}
              </div>
            </label>
          );
        })}
        {totalCount > items.length && (
          <div className="px-3 py-2 text-xs text-text-subtle border-t border-border">
            Showing {items.length} of {totalCount.toLocaleString()} — search to
            find others.
          </div>
        )}
      </div>
    </div>
  );
}
