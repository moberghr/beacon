import {
  useEffect,
  useRef,
  useState,
  type KeyboardEvent,
  type ReactNode,
} from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { ChevronDown, X } from "lucide-react";
import { Input } from "@/components/beacon";
import { cn } from "@/lib/cn";
import {
  DEFAULT_PAGE_SIZE,
  fetchPagedList,
  type ListParams,
} from "@/lib/paging";

export interface SearchPickerProps<T> {
  /** List endpoint that accepts `search`, e.g. `/beacon/api/data-sources`. */
  path: string;
  /** Extra filters sent with every search, e.g. `{ databaseOnly: true }`. */
  params?: ListParams;
  queryKey: readonly unknown[];
  value: number | null | undefined;
  /** Shown for the current value; pass it when the selection was made elsewhere (edit forms). */
  selectedLabel?: ReactNode;
  getId: (item: T) => number;
  getLabel: (item: T) => string;
  /** Secondary text in the option list, e.g. the engine type. */
  getHint?: (item: T) => ReactNode;
  /** Receives the picked item, or null when cleared. */
  onSelect: (item: T | null) => void;
  placeholder: string;
  /** Plural noun for empty states: "No data sources match …". */
  noun: string;
  /** Offers a clear option (e.g. "All data sources") instead of forcing a choice. */
  clearLabel?: string;
  disabled?: boolean;
  hasError?: boolean;
  className?: string;
  ariaLabel?: string;
}

/**
 * Search-as-you-type picker over a paged list endpoint: it never loads the whole table, only the first
 * page of matches for what the user types. Keyboard: ↑/↓ to move, Enter to pick, Esc to close.
 */
export function SearchPicker<T>({
  path,
  params,
  queryKey,
  value,
  selectedLabel,
  getId,
  getLabel,
  getHint,
  onSelect,
  placeholder,
  noun,
  clearLabel,
  disabled,
  hasError,
  className,
  ariaLabel,
}: SearchPickerProps<T>) {
  const [open, setOpen] = useState(false);
  const [term, setTerm] = useState("");
  const [debounced, setDebounced] = useState("");
  const [highlight, setHighlight] = useState(0);
  // The label of the item picked here, so the trigger shows it even once it scrolls out of the matches.
  const [pickedLabel, setPickedLabel] = useState<{
    id: number;
    label: string;
  } | null>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    const handle = window.setTimeout(() => setDebounced(term.trim()), 200);
    return () => window.clearTimeout(handle);
  }, [term]);

  const matches = useQuery({
    queryKey: [...queryKey, "picker", params ?? {}, debounced],
    queryFn: () =>
      fetchPagedList<T>(path, {
        ...params,
        search: debounced || undefined,
        pageSize: DEFAULT_PAGE_SIZE,
      }),
    placeholderData: keepPreviousData,
    enabled: open,
  });

  useEffect(() => {
    if (!open) return;
    const onDocumentClick = (e: MouseEvent) => {
      if (
        containerRef.current &&
        !containerRef.current.contains(e.target as Node)
      )
        setOpen(false);
    };
    document.addEventListener("mousedown", onDocumentClick);
    requestAnimationFrame(() => inputRef.current?.focus());
    return () => document.removeEventListener("mousedown", onDocumentClick);
  }, [open]);

  const items = matches.data?.items ?? [];
  const totalCount = matches.data?.totalCount ?? 0;
  const options: Array<T | null> = clearLabel ? [null, ...items] : items;
  const currentLabel =
    value == null
      ? null
      : pickedLabel?.id === value
        ? pickedLabel.label
        : (selectedLabel ?? `#${value}`);

  const pick = (item: T | null) => {
    setPickedLabel(item ? { id: getId(item), label: getLabel(item) } : null);
    onSelect(item);
    setOpen(false);
    setTerm("");
    setHighlight(0);
  };

  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === "ArrowDown") {
      e.preventDefault();
      setHighlight((x) => Math.min(x + 1, options.length - 1));
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      setHighlight((x) => Math.max(x - 1, 0));
    } else if (e.key === "Enter") {
      e.preventDefault();
      if (highlight < options.length) pick(options[highlight]);
    } else if (e.key === "Escape") {
      e.preventDefault();
      setOpen(false);
    }
  };

  return (
    <div ref={containerRef} className={cn("relative", className)}>
      {!open ? (
        <button
          type="button"
          disabled={disabled}
          aria-label={ariaLabel}
          aria-haspopup="listbox"
          onClick={() => setOpen(true)}
          className={cn(
            "w-full bg-surface text-text border border-border-strong rounded-sm px-2.5 py-1.5 text-sm",
            "flex items-center gap-2 text-left justify-between disabled:opacity-60",
            "focus:border-brand-500 focus:outline-none focus:shadow-ring",
            hasError && "border-crit",
          )}
        >
          <span
            className={cn(
              "truncate",
              currentLabel == null && "text-text-muted",
            )}
          >
            {currentLabel ?? clearLabel ?? placeholder}
          </span>
          <ChevronDown className="size-3.5 shrink-0 text-text-muted" />
        </button>
      ) : (
        <>
          <Input
            ref={inputRef}
            type="text"
            role="combobox"
            aria-expanded
            aria-label={ariaLabel ?? placeholder}
            value={term}
            placeholder={placeholder}
            onChange={(e) => {
              setTerm(e.target.value);
              setHighlight(0);
            }}
            onKeyDown={onKeyDown}
            autoComplete="off"
            aria-invalid={hasError}
          />
          <div
            role="listbox"
            className="absolute top-[calc(100%+4px)] left-0 right-0 z-20 bg-surface border border-border rounded-sm shadow-pop max-h-72 overflow-auto"
          >
            {matches.isLoading && (
              <div className="text-text-muted p-3 text-sm">Searching…</div>
            )}
            {!matches.isLoading &&
              options.map((item, index) => {
                const id = item == null ? null : getId(item);
                return (
                  <button
                    type="button"
                    role="option"
                    aria-selected={id === (value ?? null)}
                    key={id ?? "clear"}
                    onMouseEnter={() => setHighlight(index)}
                    onClick={() => pick(item)}
                    className={cn(
                      "w-full flex items-center justify-between gap-2 px-3 py-2 text-sm text-left",
                      index === highlight && "bg-surface-2",
                    )}
                  >
                    {item == null ? (
                      <span className="text-text-muted">{clearLabel}</span>
                    ) : (
                      <>
                        <span className="truncate">{getLabel(item)}</span>
                        {getHint && (
                          <span className="shrink-0 text-xs text-text-muted">
                            {getHint(item)}
                          </span>
                        )}
                      </>
                    )}
                  </button>
                );
              })}
            {!matches.isLoading && items.length === 0 && (
              <div className="text-text-muted p-3 text-sm">
                {debounced
                  ? `No ${noun} match "${debounced}".`
                  : `No ${noun} yet.`}
              </div>
            )}
            {totalCount > items.length && (
              <div className="px-3 py-2 text-xs text-text-subtle border-t border-border">
                Showing {items.length} of {totalCount.toLocaleString()} — keep
                typing to narrow.
              </div>
            )}
          </div>
        </>
      )}
      {!open && clearLabel && value != null && !disabled && (
        <button
          type="button"
          aria-label={`Clear ${noun}`}
          onClick={() => pick(null)}
          className="absolute right-7 top-1/2 -translate-y-1/2 text-text-muted hover:text-text"
        >
          <X className="size-3.5" />
        </button>
      )}
    </div>
  );
}
