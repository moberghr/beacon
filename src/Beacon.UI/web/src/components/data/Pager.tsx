import { ChevronLeft, ChevronRight } from "lucide-react";
import { Button } from "@/components/beacon";
import { cn } from "@/lib/cn";

export interface PagerProps {
  /** Zero-based, as the API expects; displayed one-based. */
  page: number;
  pageSize: number;
  pageCount: number;
  totalCount: number;
  /** Receives a zero-based page index. */
  onPageChange: (page: number) => void;
  className?: string;
}

/**
 * "1–20 of 4,213" plus previous / numbered / next controls. Callers pass and receive zero-based page
 * indices; the one-based display lives here only, so the two conventions cannot mix at call sites.
 */
export function Pager({
  page,
  pageSize,
  pageCount,
  totalCount,
  onPageChange,
  className,
}: PagerProps) {
  if (totalCount === 0) return null;

  const first = page * pageSize + 1;
  const last = Math.min(totalCount, first + pageSize - 1);

  return (
    <nav
      aria-label="Pagination"
      className={cn(
        "flex flex-wrap items-center justify-between gap-2 px-4 py-2.5 text-xs text-text-muted",
        className,
      )}
    >
      <span className="mono">
        {first.toLocaleString()}–{last.toLocaleString()} of{" "}
        {totalCount.toLocaleString()}
      </span>
      {pageCount > 1 && (
        <div className="flex items-center gap-1">
          <Button
            size="sm"
            variant="ghost"
            disabled={page <= 0}
            onClick={() => onPageChange(page - 1)}
            aria-label="Previous page"
          >
            <ChevronLeft className="size-3.5" />
          </Button>
          {pageWindow(page, pageCount).map((entry, index) =>
            entry === "gap" ? (
              <span key={`gap-${index}`} className="px-1 text-text-subtle">
                …
              </span>
            ) : (
              <Button
                key={entry}
                size="sm"
                variant={entry === page ? "secondary" : "ghost"}
                aria-current={entry === page ? "page" : undefined}
                aria-label={`Page ${entry + 1}`}
                onClick={() => onPageChange(entry)}
                className="min-w-7 justify-center tabular-nums"
              >
                {entry + 1}
              </Button>
            ),
          )}
          <Button
            size="sm"
            variant="ghost"
            disabled={page >= pageCount - 1}
            onClick={() => onPageChange(page + 1)}
            aria-label="Next page"
          >
            <ChevronRight className="size-3.5" />
          </Button>
        </div>
      )}
    </nav>
  );
}

/** First, last, and the current page with one neighbour each side; gaps collapse to an ellipsis. */
export function pageWindow(
  page: number,
  pageCount: number,
): Array<number | "gap"> {
  const pages = new Set(
    [0, pageCount - 1, page - 1, page, page + 1].filter(
      (x) => x >= 0 && x < pageCount,
    ),
  );
  const sorted = [...pages].sort((a, b) => a - b);
  const result: Array<number | "gap"> = [];
  for (const entry of sorted) {
    const previous = result[result.length - 1];
    if (typeof previous === "number" && entry - previous > 1) {
      result.push(entry - previous === 2 ? previous + 1 : "gap");
    }
    result.push(entry);
  }
  return result;
}
