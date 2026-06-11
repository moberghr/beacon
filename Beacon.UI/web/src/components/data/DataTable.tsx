import type { Key, ReactNode } from 'react';
import { cn } from '@/lib/cn';
import { EmptyState } from './EmptyState';

export interface Column<T> {
  key: string;
  header: ReactNode;
  render: (row: T) => ReactNode;
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
}

/**
 * Thin grid-based table. For sorting / pagination / filtering at scale, swap
 * to TanStack Table later — this is good enough for the current list pages.
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
}: DataTableProps<T>) {
  return (
    <div
      role="table"
      aria-label={ariaLabel ?? 'Data table'}
      aria-rowcount={rows.length}
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
        {columns.map(c => (
          <div role="columnheader" key={c.key}>
            {c.header}
          </div>
        ))}
      </div>

      {rows.length === 0
        ? empty ?? (
            <EmptyState
              icon={<span className="mono text-base">∅</span>}
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
              onClick={onRowClick ? () => onRowClick(row) : undefined}
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
                <div role="cell" key={c.key} className="min-w-0">
                  {c.render(row)}
                </div>
              ))}
            </div>
          ))}
    </div>
  );
}
