import type { PreviewRow } from '../queries';

interface PreviewResultsCardProps {
  title: string;
  rows: PreviewRow[];
  totalRows?: number;
  executionTimeMs?: number;
  error?: string | null;
  onClose: () => void;
}

/**
 * Renders a step or whole-query preview result as a simple HTML table.
 * Columns are derived from the union of row keys (handles sparse
 * `Dictionary<string, object?>` rows where some columns are present in
 * only some rows).
 */
export function PreviewResultsCard({
  title,
  rows,
  totalRows,
  executionTimeMs,
  error,
  onClose,
}: PreviewResultsCardProps) {
  const columns = collectColumns(rows);
  const shownRows = rows.slice(0, 100);
  const truncated = rows.length > shownRows.length;

  return (
    <div className="card">
      <div className="card__head">
        <h3 className="card__title">{title}</h3>
        <span className="card__sub">
          {error
            ? 'Failed'
            : `${rows.length} row${rows.length === 1 ? '' : 's'}` +
              (totalRows != null && totalRows !== rows.length ? ` of ${totalRows}` : '') +
              (executionTimeMs != null ? ` · ${executionTimeMs.toFixed(1)} ms` : '')}
        </span>
        <div className="card__actions">
          <button className="btn" type="button" onClick={onClose}>
            Close
          </button>
        </div>
      </div>
      <div className="card__body">
        {error ? (
          <div className="q-error" role="alert">
            {error}
          </div>
        ) : rows.length === 0 ? (
          <div className="muted">No rows returned.</div>
        ) : (
          <div style={{ overflowX: 'auto', maxHeight: 480 }}>
            <table className="data-table">
              <thead>
                <tr>
                  {columns.map(c => (
                    <th key={c}>{c}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {shownRows.map((row, idx) => (
                  <tr key={idx}>
                    {columns.map(c => (
                      <td key={c}>{formatCell(row[c])}</td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
            {truncated && (
              <div className="muted" style={{ marginTop: 8, fontSize: 12 }}>
                Showing first 100 of {rows.length} rows.
              </div>
            )}
          </div>
        )}
      </div>
    </div>
  );
}

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

function formatCell(value: unknown): string {
  if (value == null) return 'NULL';
  if (typeof value === 'object') return JSON.stringify(value);
  return String(value);
}
