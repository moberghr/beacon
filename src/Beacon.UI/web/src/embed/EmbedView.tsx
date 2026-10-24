import { useEffect, useRef, useState } from 'react';
import { refusalMeansExpired, renderEmbed, type EmbedRender } from './embedClient';

type ViewState = { kind: 'loading' } | { kind: 'ok'; data: EmbedRender } | { kind: 'expired' } | { kind: 'unavailable' };

interface EmbedViewProps {
  /** The embed token, from the fragment; null when the page was opened without one. */
  token: string | null;
  /** The frame marker of the served page; null when the page was not served by Beacon's embed route. */
  frameMarker: string | null;
  /** Tells the framing page the token has expired; at most once per page load (see `expiryNotifier`). */
  onExpired?: () => void;
}

/**
 * A read-only view of one embedded query: its title, columns and rows. With a token and a frame marker it always asks
 * Beacon, which alone decides whether the token is still valid; the viewer's clock only words a refusal.
 */
export function EmbedView({ token, frameMarker, onExpired }: EmbedViewProps) {
  const [state, setState] = useState<ViewState>(() =>
    token == null || frameMarker == null ? { kind: 'unavailable' } : { kind: 'loading' },
  );
  const renderedBefore = useRef(false);

  useEffect(() => {
    if (token == null || frameMarker == null) {
      return;
    }

    const controller = new AbortController();
    renderEmbed(token, frameMarker, controller.signal).then(
      outcome => {
        if (outcome.kind === 'ok') {
          renderedBefore.current = true;
          setState(outcome);
          return;
        }

        setState(
          outcome.kind === 'refused' && refusalMeansExpired(token, renderedBefore.current)
            ? { kind: 'expired' }
            : { kind: 'unavailable' },
        );
      },
      () => {
        // Aborted: the view is gone.
      },
    );

    return () => controller.abort();
    // One render per page load: a new token comes with a new page.
  }, [token, frameMarker]);

  useEffect(() => {
    if (state.kind === 'expired') {
      onExpired?.();
    }
  }, [state.kind, onExpired]);

  if (state.kind === 'loading') {
    return <Notice title="Loading…" />;
  }

  if (state.kind === 'expired') {
    // The token has left the address, so reloading this frame cannot bring it back: only the page showing it can.
    return <Notice title="This view has expired" sub="The page showing it needs to refresh it." />;
  }

  if (state.kind === 'unavailable') {
    return <Notice title="This view is unavailable" />;
  }

  return <ResultTable data={state.data} />;
}

function Notice({ title, sub }: { title: string; sub?: string }) {
  return (
    <div role="status" className="flex min-h-[8rem] flex-col items-center justify-center gap-1 p-6 text-center">
      <p className="text-sm font-semibold text-text">{title}</p>
      {sub && <p className="text-xs text-text-muted">{sub}</p>}
    </div>
  );
}

/** A row's own value of the column; never a property it inherits (`constructor`, `toString`, …). */
function valueOf(row: Record<string, unknown>, column: string): unknown {
  return Object.hasOwn(row, column) ? row[column] : null;
}

function ResultTable({ data }: { data: EmbedRender }) {
  return (
    <div className="flex flex-col gap-2 p-4">
      {data.title && <h1 className="text-base font-semibold text-text">{data.title}</h1>}
      <div className="overflow-auto rounded-md border border-border bg-surface">
        <table className="w-full border-collapse text-sm">
          <thead className="bg-surface-2">
            <tr>
              {/* Keyed by position: a result may name two columns alike (SELECT a.id, b.id). */}
              {data.columns.map((column, index) => (
                <th
                  key={index}
                  scope="col"
                  className="whitespace-nowrap border-b border-border px-3 py-2 text-left text-xs font-semibold text-text-muted"
                >
                  {column}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {data.rows.length === 0 ? (
              <tr>
                <td colSpan={Math.max(data.columns.length, 1)} className="px-3 py-6 text-center text-text-muted">
                  No rows
                </td>
              </tr>
            ) : (
              data.rows.map((row, rowIndex) => (
                <tr key={rowIndex} className="border-b border-border last:border-b-0">
                  {data.columns.map((column, index) => (
                    <td key={index} className="whitespace-nowrap px-3 py-1.5 text-text">
                      <Cell value={valueOf(row, column)} />
                    </td>
                  ))}
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>
      {data.truncated && (
        <p className="text-xs text-text-muted">
          Truncated at {data.maxRows.toLocaleString()} {data.maxRows === 1 ? 'row' : 'rows'}.
        </p>
      )}
    </div>
  );
}

function Cell({ value }: { value: unknown }) {
  if (value === null || value === undefined) {
    return <span className="text-text-subtle">—</span>;
  }

  return <>{typeof value === 'object' ? JSON.stringify(value) : String(value)}</>;
}
