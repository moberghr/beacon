import {
  Card,
  CardHead,
  CardTitle,
  CardSub,
  CardActions,
  CardBody,
  Button,
  Banner,
} from '@/components/beacon';
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
    <Card>
      <CardHead>
        <CardTitle>{title}</CardTitle>
        <CardSub>
          {error
            ? 'Failed'
            : `${rows.length} row${rows.length === 1 ? '' : 's'}` +
              (totalRows != null && totalRows !== rows.length ? ` of ${totalRows}` : '') +
              (executionTimeMs != null ? ` · ${executionTimeMs.toFixed(1)} ms` : '')}
        </CardSub>
        <CardActions>
          <Button onClick={onClose}>Close</Button>
        </CardActions>
      </CardHead>
      <CardBody flush>
        {error ? (
          <div className="p-4">
            <Banner tone="crit" title={error} role="alert" />
          </div>
        ) : rows.length === 0 ? (
          <div className="p-4 text-text-muted">No rows returned.</div>
        ) : (
          <div className="overflow-x-auto max-h-[480px]">
            <table className="w-full border-collapse text-xs">
              <thead>
                <tr>
                  {columns.map(c => (
                    <th
                      key={c}
                      className="text-left px-3.5 py-2.5 mono font-semibold uppercase tracking-eyebrow text-text-muted bg-surface-2 border-b border-border whitespace-nowrap"
                    >
                      {c}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {shownRows.map((row, idx) => (
                  <tr key={idx} className="hover:bg-surface-2">
                    {columns.map(c => (
                      <td key={c} className="px-3.5 py-2.5 mono border-b border-border">
                        {formatCell(row[c])}
                      </td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
            {truncated && (
              <div className="text-text-muted text-xs px-4 py-2">
                Showing first 100 of {rows.length} rows.
              </div>
            )}
          </div>
        )}
      </CardBody>
    </Card>
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
