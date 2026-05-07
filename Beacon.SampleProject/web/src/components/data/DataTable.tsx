import type { ReactNode } from 'react';
import type { Key } from 'react';

export interface Column<T> {
  key: string;
  header: string;
  render: (row: T) => ReactNode;
}

interface DataTableProps<T> {
  columns: Column<T>[];
  rows: T[];
  rowKey: (row: T) => Key;
  /** CSS grid-template-columns string. Length must match columns + any trailing decorations. */
  gridTemplate: string;
  onRowClick?: (row: T) => void;
  empty?: ReactNode;
}

/**
 * Thin table built on the design system's `.tbl` grid pattern.
 * For sorting / pagination / filtering at scale, swap to TanStack Table later.
 * For Phase 3 Batch 1 the design-system grid is enough.
 */
export function DataTable<T>({ columns, rows, rowKey, gridTemplate, onRowClick, empty }: DataTableProps<T>) {
  return (
    <div className="tbl">
      <div className="tbl__head" style={{ gridTemplateColumns: gridTemplate }}>
        {columns.map(c => <div key={c.key}>{c.header}</div>)}
      </div>

      {rows.length === 0
        ? (empty ?? <DefaultEmpty />)
        : rows.map(row => (
          <div
            key={rowKey(row)}
            className="tbl__row"
            style={{
              gridTemplateColumns: gridTemplate,
              display: 'grid',
              gap: 10,
              padding: '12px 16px',
              borderBottom: '1px solid var(--border)',
              alignItems: 'center',
              cursor: onRowClick ? 'pointer' : 'default',
            }}
            onClick={onRowClick ? () => onRowClick(row) : undefined}
            role={onRowClick ? 'button' : undefined}
            tabIndex={onRowClick ? 0 : undefined}
            onKeyDown={onRowClick ? e => {
              if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                onRowClick(row);
              }
            } : undefined}
          >
            {columns.map(c => <div key={c.key}>{c.render(row)}</div>)}
          </div>
        ))}
    </div>
  );
}

function DefaultEmpty() {
  return (
    <div className="empty-state" style={{ margin: 16 }}>
      <div className="empty-state__icon">∅</div>
      <div>
        <div className="empty-state__title">Nothing here yet</div>
        <div className="empty-state__sub">Items will appear once they're created.</div>
      </div>
    </div>
  );
}
