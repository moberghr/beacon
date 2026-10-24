import { useEffect, useState } from 'react';
import { EXPIRED_MESSAGE, isExpired, renderEmbed, type EmbedRender, type RenderOutcome } from './embedClient';

type ViewState = { kind: 'loading' } | RenderOutcome;

interface EmbedViewProps {
  /** The embed token, from the fragment; null when the page was opened without one. */
  token: string | null;
  /** The frame marker of the served page; null when the page was not served by Beacon's embed route. */
  frameMarker: string | null;
  /** Where the expiry notice goes: the framing page. Null when the view is not framed. */
  host?: Pick<Window, 'postMessage'> | null;
}

/** A read-only view of one embedded query: its title, columns and rows, from a single render call. */
export function EmbedView({ token, frameMarker, host = framingWindow() }: EmbedViewProps) {
  const [state, setState] = useState<ViewState>(() =>
    token == null || frameMarker == null
      ? { kind: 'unavailable' }
      : isExpired(token)
        ? { kind: 'expired' }
        : { kind: 'loading' },
  );

  useEffect(() => {
    if (state.kind !== 'loading' || token == null || frameMarker == null) {
      return;
    }

    const controller = new AbortController();
    renderEmbed(token, frameMarker, controller.signal).then(
      outcome => setState(outcome),
      () => {
        // Aborted: the view is gone.
      },
    );

    return () => controller.abort();
    // One render per page load: a new token comes with a new page.
  }, [token, frameMarker]);

  useEffect(() => {
    if (state.kind === 'expired') {
      // Carries nothing but its type, so any framing origin may read it.
      host?.postMessage(EXPIRED_MESSAGE, '*');
    }
  }, [state.kind, host]);

  if (state.kind === 'loading') {
    return <Notice title="Loading…" />;
  }

  if (state.kind === 'expired') {
    return <Notice title="This view has expired" sub="Reload the page to see it again." />;
  }

  if (state.kind === 'unavailable') {
    return <Notice title="This view is unavailable" />;
  }

  return <ResultTable data={state.data} />;
}

function framingWindow(): Window | null {
  return window.parent !== window ? window.parent : null;
}

function Notice({ title, sub }: { title: string; sub?: string }) {
  return (
    <div role="status" className="flex min-h-[8rem] flex-col items-center justify-center gap-1 p-6 text-center">
      <p className="text-sm font-semibold text-text">{title}</p>
      {sub && <p className="text-xs text-text-muted">{sub}</p>}
    </div>
  );
}

function ResultTable({ data }: { data: EmbedRender }) {
  return (
    <div className="flex flex-col gap-2 p-4">
      {data.title && <h1 className="text-base font-semibold text-text">{data.title}</h1>}
      <div className="overflow-auto rounded-md border border-border bg-surface">
        <table className="w-full border-collapse text-sm">
          <thead className="bg-surface-2">
            <tr>
              {data.columns.map(column => (
                <th
                  key={column}
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
              data.rows.map((row, index) => (
                <tr key={index} className="border-b border-border last:border-b-0">
                  {data.columns.map(column => (
                    <td key={column} className="whitespace-nowrap px-3 py-1.5 text-text">
                      <Cell value={row[column]} />
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
