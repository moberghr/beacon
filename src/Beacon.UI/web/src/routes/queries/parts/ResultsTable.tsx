import { ArrowDown, ArrowUp, ArrowUpDown } from 'lucide-react';
import { cn } from '@/lib/cn';
import type { SortState } from '@/lib/paging';
import type { PreviewRow } from '../queries';

const MAX_RENDERED_ROWS = 100;

interface ResultsTableProps {
  rows: PreviewRow[];
  /** Tailwind max-height class for the scroll area; the header stays pinned inside it. */
  maxHeightClass?: string;
  /** Rows before this page, so row numbers continue across pages. */
  rowOffset?: number;
  /** Server-side sort; with `onSortChange` every header becomes a sort button. */
  sort?: SortState | null;
  onSortChange?: (column: string) => void;
}

/**
 * Preview rows as a table: pinned header, row numbers, right-aligned numeric columns, dimmed NULLs.
 * Columns are the union of row keys in first-seen order, because `Dictionary<string, object?>` rows
 * can be sparse.
 */
export function ResultsTable({
  rows,
  maxHeightClass = 'max-h-[480px]',
  rowOffset = 0,
  sort,
  onSortChange,
}: ResultsTableProps) {
  const columns = collectColumns(rows);
  const shownRows = rows.slice(0, MAX_RENDERED_ROWS);
  const numeric = numericColumns(columns, shownRows);

  return (
    <div className={cn('overflow-auto', maxHeightClass)}>
      <table className="w-full border-collapse text-xs">
        <thead className="sticky top-0 z-[1]">
          <tr>
            <th className={cn(HEAD_CELL, 'w-10 text-right')}>#</th>
            {columns.map(x => {
              const active = sort?.column === x;
              return (
                <th
                  key={x}
                  className={cn(HEAD_CELL, numeric.has(x) && 'text-right')}
                  aria-sort={active ? (sort!.direction === 'asc' ? 'ascending' : 'descending') : undefined}
                >
                  {onSortChange ? (
                    <button
                      type="button"
                      onClick={() => onSortChange(x)}
                      className={cn(
                        'inline-flex items-center gap-1 uppercase tracking-eyebrow hover:text-text',
                        active && 'text-text',
                        numeric.has(x) && 'flex-row-reverse',
                      )}
                    >
                      {x}
                      {active ? (
                        sort!.direction === 'asc' ? <ArrowUp className="size-3" /> : <ArrowDown className="size-3" />
                      ) : (
                        <ArrowUpDown className="size-3 opacity-40" />
                      )}
                    </button>
                  ) : (
                    x
                  )}
                </th>
              );
            })}
          </tr>
        </thead>
        <tbody>
          {shownRows.map((row, index) => (
            <tr key={index} className="hover:bg-surface-2">
              <td className={cn(BODY_CELL, 'text-right text-text-subtle')}>{rowOffset + index + 1}</td>
              {columns.map(x => {
                const value = row[x];
                return (
                  <td
                    key={x}
                    className={cn(
                      BODY_CELL,
                      numeric.has(x) && 'text-right tabular-nums',
                      value == null && 'text-text-subtle italic',
                    )}
                  >
                    {formatCell(value)}
                  </td>
                );
              })}
            </tr>
          ))}
        </tbody>
      </table>
      {rows.length > shownRows.length && (
        <div className="text-text-muted text-xs px-4 py-2">
          Showing first {shownRows.length} of {rows.length} rows.
        </div>
      )}
    </div>
  );
}

/** Tab-separated with a header row, so a paste into Excel or Sheets lands in cells. */
export function rowsToTsv(rows: PreviewRow[]): string {
  const columns = collectColumns(rows);
  const clean = (value: unknown) => formatCell(value).replace(/[\t\r\n]+/g, ' ');
  return [columns.join('\t'), ...rows.map(x => columns.map(y => clean(x[y])).join('\t'))].join('\n');
}

const HEAD_CELL =
  'text-left px-3.5 py-2.5 mono font-semibold uppercase tracking-eyebrow text-text-muted bg-surface-2 border-b border-border whitespace-nowrap';
const BODY_CELL = 'px-3.5 py-2 mono border-b border-border whitespace-nowrap';

function collectColumns(rows: PreviewRow[]): string[] {
  const seen = new Set<string>();
  const ordered: string[] = [];
  for (const row of rows) {
    for (const key of Object.keys(row)) {
      if (!seen.has(key)) {
        seen.add(key);
        ordered.push(key);
      }
    }
  }
  return ordered;
}

// A column reads as numeric (header and cells right-aligned) when every non-null value is a number.
function numericColumns(columns: string[], rows: PreviewRow[]): Set<string> {
  return new Set(
    columns.filter(x => {
      const values = rows.map(y => y[x]).filter(y => y != null);
      return values.length > 0 && values.every(y => typeof y === 'number');
    }),
  );
}

function formatCell(value: unknown): string {
  if (value == null) return 'NULL';
  if (typeof value === 'object') return JSON.stringify(value);
  return String(value);
}
