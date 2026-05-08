import { lazy, Suspense } from 'react';

// Monaco is heavy — keep it out of the main bundle. The default `loading`
// hook is enough; React.lazy ensures the chunk is only fetched when an
// editor instance actually mounts (i.e. when a user opens the QueryEditor).
const Monaco = lazy(() =>
  import('@monaco-editor/react').then(m => ({ default: m.default })),
);

export interface SqlEditorProps {
  /** Current SQL text. Editor is fully controlled. */
  value: string;
  /** Fired with the next value on every change (Monaco coalesces typing). */
  onChange: (next: string) => void;
  /** Pixel height. Width tracks the parent. Default 360px. */
  height?: number | string;
  /** Read-only render (preview / version diff). Default false. */
  readOnly?: boolean;
  /** Optional id used by Monaco for editor instance disambiguation. */
  id?: string;
}

/**
 * Lazy-loaded SQL editor used by the React Query Editor (Phase 3 Batch 5f).
 *
 * Notes:
 * - We deliberately avoid the Monaco completion-provider dance from the Blazor
 *   editor; metadata-driven IntelliSense is deferred. SQL syntax highlighting
 *   alone covers the bulk of the value users got from the legacy editor.
 * - Theme is `vs` (light) to match the rest of beacon-design's surfaces.
 * - The fallback is a styled placeholder so layout doesn't reflow when Monaco
 *   resolves.
 */
export function SqlEditor({ value, onChange, height = 360, readOnly = false, id }: SqlEditorProps) {
  return (
    <div
      className="sql-editor"
      style={{
        height,
        border: '1px solid var(--border)',
        borderRadius: 6,
        overflow: 'hidden',
        background: 'var(--surface-2, #f7f7fa)',
      }}
    >
      <Suspense
        fallback={
          <div
            className="muted"
            style={{
              padding: 12,
              fontSize: 12,
              fontFamily: 'var(--mono-font, ui-monospace, SFMono-Regular, monospace)',
            }}
          >
            Loading editor…
          </div>
        }
      >
        <Monaco
          height="100%"
          defaultLanguage="sql"
          theme="vs"
          value={value}
          onChange={next => onChange(next ?? '')}
          path={id}
          options={{
            minimap: { enabled: false },
            fontSize: 13,
            lineNumbers: 'on',
            renderLineHighlight: 'all',
            scrollBeyondLastLine: false,
            automaticLayout: true,
            readOnly,
            wordWrap: 'on',
          }}
        />
      </Suspense>
    </div>
  );
}

export default SqlEditor;
