import type { Key, ReactNode } from 'react';
import { ArrowDown, ArrowUp, ArrowUpDown, Inbox } from 'lucide-react';
import { cn } from '@/lib/cn';
import type { SortState } from '@/lib/paging';
import { EmptyState } from './EmptyState';
import { Pager, type PagerProps } from './Pager';

export interface Column<T> {
  key: string;
  header: ReactNode;
  render: (row: T) => ReactNode;
  /** The server-side sort column (a property name of the row); makes the header a sort button. */
  sortKey?: string;
  /** Right-align the header and cells, for numbers. */
  align?: 'left' | 'right';
}

interface DataTableProps<T> {
  columns: Column<T>[];
  rows: T[];
  rowKey: (row: T, index: number) => Key;
  /** CSS grid-template-columns string for both header and rows. */
  gridTemplate: string;
  onRowClick?: (row: T) => void;
  empty?: ReactNode;
  className?: string;
  /** Accessible name for the table, announced by screen readers. */
  ariaLabel?: string;
  /** Current server sort; with `onSortChange`, columns that have a `sortKey` become sortable. */
  sort?: SortState | null;
  onSortChange?: (column: string) => void;
  /** Server paging; renders the pager under the rows. */
  paging?: Omit<PagerProps, 'className'>;
  /** Dims the rows while the next page or sort loads (the previous rows stay visible meanwhile). */
  loading?: boolean;
}

/**
 * Grid-based table. Sorting and paging are server-side: pass `sort` / `onSortChange` / `paging`, usually
 * by spreading `usePagedList(...).tableProps`. The table itself never reorders or slices rows.
 */
export function DataTable<T>({
  columns,
  rows,
  rowKey,
  gridTemplate,
  onRowClick,
  empty,
  className,
  ariaLabel,
  sort,
  onSortChange,
  paging,
  loading,
}: DataTableProps<T>) {
  return (
    <div
      role="table"
      aria-label={ariaLabel ?? 'Data table'}
      aria-rowcount={(paging?.totalCount ?? rows.length) + 1}
      aria-busy={loading || undefined}
      className={cn(
        'bg-surface border border-border rounded-md overflow-hidden shadow-sm',
        className,
      )}
    >
      <div
        role="row"
        className="grid gap-2.5 px-4 py-2 bg-surface-2 border-b border-border text-2xs font-semibold uppercase tracking-eyebrow text-text-muted"
        style={{ gridTemplateColumns: gridTemplate }}
      >
        {columns.map(c => {
          const sortable = c.sortKey != null && onSortChange != null;
          const active = sortable && sort?.column.toLowerCase() === c.sortKey!.toLowerCase();
          return (
            <div
              role="columnheader"
              key={c.key}
              aria-sort={active ? (sort!.direction === 'asc' ? 'ascending' : 'descending') : undefined}
              className={cn(c.align === 'right' && 'text-right')}
            >
              {sortable ? (
                <button
                  type="button"
                  onClick={() => onSortChange!(c.sortKey!)}
                  className={cn(
                    'inline-flex items-center gap-1 uppercase tracking-eyebrow hover:text-text',
                    active && 'text-text',
                    c.align === 'right' && 'flex-row-reverse',
                  )}
                >
                  {c.header}
                  {active ? (
                    sort!.direction === 'asc' ? <ArrowUp className="size-3" /> : <ArrowDown className="size-3" />
                  ) : (
                    <ArrowUpDown className="size-3 opacity-40" />
                  )}
                </button>
              ) : (
                c.header
              )}
            </div>
          );
        })}
      </div>

      <div className={cn(loading && 'opacity-60 transition-opacity')}>
        {rows.length === 0
          ? empty ?? (
              <EmptyState
                icon={<Inbox />}
                title="Nothing here yet"
                description="Items will appear once they're created."
              />
            )
          : rows.map((row, index) => (
              <div
                key={rowKey(row, index)}
                role="row"
                className={cn(
                  'grid gap-2.5 px-4 py-3 border-b border-border last:border-b-0 items-center text-sm',
                  onRowClick && 'cursor-pointer hover:bg-surface-2',
                )}
                style={{ gridTemplateColumns: gridTemplate }}
                onClick={
                  onRowClick
                    ? e => {
                        // Ignore clicks on interactive content inside cells —
                        // the cell control owns that click, not the row.
                        const interactive = (e.target as HTMLElement).closest(
                          'button, a, input, select, textarea, [role="button"]',
                        );
                        if (interactive && e.currentTarget.contains(interactive)) {
                          return;
                        }
                        onRowClick(row);
                      }
                    : undefined
                }
                tabIndex={onRowClick ? 0 : undefined}
                onKeyDown={
                  onRowClick
                    ? e => {
                        if (e.key === 'Enter' || e.key === ' ') {
                          e.preventDefault();
                          onRowClick(row);
                        }
                      }
                    : undefined
                }
              >
                {columns.map(c => (
                  <div role="cell" key={c.key} className={cn('min-w-0', c.align === 'right' && 'text-right tabular-nums')}>
                    {c.render(row)}
                  </div>
                ))}
              </div>
            ))}
      </div>
      {paging && <Pager {...paging} className="border-t border-border" />}
    </div>
  );
}
