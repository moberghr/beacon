import { lazy, Suspense, useEffect, useRef, useState } from 'react';
import type { DatabaseMetadataSnapshot } from '@/routes/data-sources/queries';

// Monaco is heavy — keep it out of the main bundle. The default `loading`
// hook is enough; React.lazy ensures the chunk is only fetched when an
// editor instance actually mounts (i.e. when a user opens the QueryEditor).
//
// Bundle monaco-editor locally instead of letting @monaco-editor/react fetch
// it from cdn.jsdelivr.net. Edge's Tracking Prevention blocks storage access
// for the CDN, which breaks codicon font loading and suggest widget styling.
async function configureLocalMonaco(reactMod: typeof import('@monaco-editor/react')) {
  // Tests mock @monaco-editor/react with a stub that has no `loader` export;
  // vitest throws when we access undeclared members, so guard via try/catch.
  let loader: typeof reactMod.loader | undefined;
  try {
    loader = reactMod.loader;
  } catch {
    return;
  }
  if (!loader) return;
  const [monaco, editorWorkerMod] = await Promise.all([
    import('monaco-editor'),
    import('monaco-editor/esm/vs/editor/editor.worker?worker'),
  ]);
  const EditorWorker = (editorWorkerMod as { default: new () => Worker }).default;
  (self as unknown as { MonacoEnvironment: { getWorker: () => Worker } }).MonacoEnvironment = {
    getWorker: () => new EditorWorker(),
  };
  loader.config({
    monaco: monaco as unknown as Parameters<typeof loader.config>[0]['monaco'],
  });
}

const Monaco = lazy(async () => {
  const reactMod = await import('@monaco-editor/react');
  await configureLocalMonaco(reactMod);
  return { default: reactMod.default };
});

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
  /** Optional metadata used to drive table/column completion. */
  metadata?: DatabaseMetadataSnapshot | null;
  /** Optional list of parameter names ({name}) for completion. */
  parameterNames?: string[];
  /** How many earlier-step results to suggest under `@result…`. */
  crossStepResultCount?: number;
  /** Imperative handle: a callback fired once Monaco is ready. */
  onEditorReady?: (editor: MonacoEditorLike) => void;
}

/**
 * Minimal duck-typed view of `monaco.editor.IStandaloneCodeEditor` we use
 * from page-level code (cursor inserts). Avoids importing monaco types
 * eagerly.
 */
export interface MonacoEditorLike {
  getPosition(): { lineNumber: number; column: number } | null;
  executeEdits(source: string, edits: unknown[]): boolean;
  focus(): void;
}

interface CompletionState {
  metadata: DatabaseMetadataSnapshot | null | undefined;
  parameterNames: string[];
  crossStepResultCount: number;
}

// Monaco accumulates completion providers across page navigations if we
// register on every mount — keep one global registration keyed by language.
// Each mounted editor registers its own snapshot keyed by its model URI so
// multiple editors on one page (e.g. one per query step) don't clobber each
// other; the fallback ref covers models that haven't registered yet.
let providerRegistered = false;
const completionStates = new Map<string, CompletionState>();
const fallbackCompletionState: { current: CompletionState } = {
  current: { metadata: null, parameterNames: [], crossStepResultCount: 0 },
};

const SQL_KEYWORDS = [
  'SELECT', 'FROM', 'WHERE', 'GROUP BY', 'ORDER BY', 'LIMIT',
  'JOIN', 'LEFT JOIN', 'INNER JOIN', 'RIGHT JOIN', 'FULL JOIN',
  'ON', 'AS', 'WITH', 'AND', 'OR', 'NOT',
  'IS NULL', 'IS NOT NULL', 'IN', 'BETWEEN', 'LIKE',
  'COUNT', 'SUM', 'AVG', 'MIN', 'MAX', 'DISTINCT',
  'DATE_TRUNC', 'COALESCE', 'NULLIF', 'CASE', 'WHEN', 'THEN', 'ELSE', 'END',
  'INSERT', 'INTO', 'UPDATE', 'SET', 'DELETE', 'VALUES',
];

const TABLE_TRIGGER_KEYWORDS = ['FROM', 'JOIN', 'INTO', 'UPDATE', 'TABLE'];

interface MonacoLikeNamespace {
  languages: {
    registerCompletionItemProvider: (
      lang: string,
      provider: unknown,
    ) => { dispose(): void };
    CompletionItemKind: Record<string, number>;
    CompletionItemInsertTextRule: Record<string, number>;
  };
}

function registerSqlCompletionProvider(monaco: MonacoLikeNamespace) {
  if (providerRegistered) return;
  providerRegistered = true;

  const Kind = monaco.languages.CompletionItemKind;

  monaco.languages.registerCompletionItemProvider('sql', {
    triggerCharacters: [' ', '.', '{', '@'],
    provideCompletionItems: (model: unknown, position: { lineNumber: number; column: number }) => {
      // Use a duck-typed model interface to avoid importing monaco types.
      const m = model as {
        getWordUntilPosition: (p: { lineNumber: number; column: number }) => {
          word: string;
          startColumn: number;
          endColumn: number;
        };
        getValueInRange: (range: {
          startLineNumber: number;
          startColumn: number;
          endLineNumber: number;
          endColumn: number;
        }) => string;
        getLineContent: (line: number) => string;
        uri?: { toString(): string };
      };

      const word = m.getWordUntilPosition(position);
      const range = {
        startLineNumber: position.lineNumber,
        startColumn: word.startColumn,
        endLineNumber: position.lineNumber,
        endColumn: word.endColumn,
      };

      const lineText = m.getLineContent(position.lineNumber);
      const beforeCursor = lineText.slice(0, position.column - 1);

      const modelUri = m.uri?.toString();
      const { metadata, parameterNames, crossStepResultCount } =
        (modelUri ? completionStates.get(modelUri) : undefined) ?? fallbackCompletionState.current;
      const tables = metadata?.tables ?? [];
      const suggestions: unknown[] = [];

      // {paramName}
      const lastBrace = beforeCursor.lastIndexOf('{');
      const lastSpaceBeforeBrace = Math.max(
        beforeCursor.lastIndexOf(' '),
        beforeCursor.lastIndexOf('\n'),
        beforeCursor.lastIndexOf('}'),
      );
      if (lastBrace > lastSpaceBeforeBrace && parameterNames.length > 0) {
        for (const name of parameterNames) {
          suggestions.push({
            label: `{${name}}`,
            kind: Kind.Variable,
            insertText: `${name}}`,
            detail: 'parameter',
            range,
            sortText: '0' + name,
          });
        }
      }

      // @resultN
      const lastAt = beforeCursor.lastIndexOf('@');
      const lastSpaceBeforeAt = Math.max(
        beforeCursor.lastIndexOf(' ', lastAt - 1),
        beforeCursor.lastIndexOf('\n', lastAt - 1),
      );
      if (lastAt > lastSpaceBeforeAt && lastAt >= 0 && crossStepResultCount > 0) {
        for (let i = 1; i <= crossStepResultCount; i++) {
          suggestions.push({
            label: `@result${i}`,
            kind: Kind.Reference,
            insertText: `result${i}`,
            detail: 'cross-step result',
            range,
            sortText: '1' + String(i).padStart(3, '0'),
          });
        }
      }

      // <table>.<column>
      const dotMatch = beforeCursor.match(/([A-Za-z_][\w]*(?:\.[A-Za-z_][\w]*)?)\.([\w]*)$/);
      if (dotMatch && tables.length > 0) {
        const ident = dotMatch[1];
        const parts = ident.split('.');
        let target;
        if (parts.length === 2) {
          target = tables.find(
            t => t.schemaName.toLowerCase() === parts[0].toLowerCase() &&
                 t.tableName.toLowerCase() === parts[1].toLowerCase(),
          );
        } else {
          target = tables.find(t => t.tableName.toLowerCase() === parts[0].toLowerCase());
        }
        if (target) {
          for (const col of target.columns) {
            suggestions.push({
              label: col.columnName,
              kind: Kind.Field,
              insertText: col.columnName,
              detail: col.dataType + (col.isPrimaryKey ? ' · PK' : ''),
              documentation: col.description ?? undefined,
              range,
              sortText: '2' + String(col.ordinalPosition).padStart(4, '0'),
            });
          }
          return { suggestions };
        }
      }

      // Table suggestions after FROM/JOIN/etc.
      const lookback = beforeCursor.slice(-30).toUpperCase();
      const triggersTable = TABLE_TRIGGER_KEYWORDS.some(kw =>
        new RegExp(`\\b${kw}\\s+\\w*$`).test(lookback),
      );
      if (triggersTable && tables.length > 0) {
        for (const t of tables) {
          const fqn = `${t.schemaName}.${t.tableName}`;
          suggestions.push({
            label: fqn,
            kind: Kind.Struct,
            insertText: fqn,
            detail: `${t.columns.length} cols`,
            range,
            sortText: '3' + fqn,
          });
        }
      }

      // Always include keywords + bare table names as fallback.
      for (const kw of SQL_KEYWORDS) {
        suggestions.push({
          label: kw,
          kind: Kind.Keyword,
          insertText: kw,
          range,
          sortText: '5' + kw,
        });
      }
      for (const t of tables) {
        suggestions.push({
          label: t.tableName,
          kind: Kind.Struct,
          insertText: `${t.schemaName}.${t.tableName}`,
          detail: `${t.schemaName} · ${t.columns.length} cols`,
          range,
          sortText: '4' + t.tableName,
        });
      }

      return { suggestions };
    },
  });
}

/**
 * Lazy-loaded SQL editor used by the React Query Editor (Phase 3 Batch 5f).
 *
 * Theme is `vs` (light) to match the rest of beacon-design's surfaces.
 * The fallback is a styled placeholder so layout doesn't reflow when Monaco
 * resolves.
 *
 * Completion provider is registered once globally per language and reads
 * the latest props snapshot from a module-scope map keyed by model URI —
 * that prevents accumulating providers across page navigations, keeps
 * suggestions fresh without re-registering on every prop change, and lets
 * multiple editors on one page keep independent metadata.
 */
export function SqlEditor({
  value,
  onChange,
  height = 360,
  readOnly = false,
  id,
  metadata = null,
  parameterNames = [],
  crossStepResultCount = 0,
  onEditorReady,
}: SqlEditorProps) {
  // Register this editor's snapshot under its model URI so the global
  // completion provider resolves the right metadata per editor instance.
  // The fallback covers the window before the model URI is known.
  const [modelUri, setModelUri] = useState<string | null>(null);

  useEffect(() => {
    const snapshot: CompletionState = {
      metadata,
      parameterNames,
      crossStepResultCount,
    };
    fallbackCompletionState.current = snapshot;
    if (modelUri) {
      completionStates.set(modelUri, snapshot);
    }
  }, [metadata, parameterNames, crossStepResultCount, modelUri]);

  useEffect(() => {
    if (!modelUri) {
      return;
    }
    return () => {
      completionStates.delete(modelUri);
    };
  }, [modelUri]);

  const onEditorReadyRef = useRef(onEditorReady);
  useEffect(() => {
    onEditorReadyRef.current = onEditorReady;
  }, [onEditorReady]);

  return (
    <div
      className="border border-border rounded-sm overflow-hidden bg-surface-2 min-w-0 w-full"
      style={{ height }}
    >
      <Suspense
        fallback={
          <div className="p-3 text-xs mono text-text-muted">Loading editor…</div>
        }
      >
        <Monaco
          height="100%"
          defaultLanguage="sql"
          theme="vs"
          value={value}
          onChange={next => onChange(next ?? '')}
          path={id}
          onMount={(editor, monaco) => {
            registerSqlCompletionProvider(monaco as unknown as MonacoLikeNamespace);
            const model = (editor as unknown as {
              getModel?: () => { uri?: { toString(): string } } | null;
            }).getModel?.();
            setModelUri(model?.uri?.toString() ?? null);
            const cb = onEditorReadyRef.current;
            if (cb) {
              cb(editor as unknown as MonacoEditorLike);
            }
          }}
          options={{
            minimap: { enabled: false },
            fontSize: 13,
            lineNumbers: 'on',
            renderLineHighlight: 'all',
            scrollBeyondLastLine: false,
            automaticLayout: true,
            readOnly,
            wordWrap: 'on',
            quickSuggestions: { other: true, comments: false, strings: true },
            fixedOverflowWidgets: true,
            suggestFontSize: 13,
            suggestLineHeight: 22,
            suggest: {
              showIcons: true,
              showStatusBar: false,
              previewMode: 'prefix',
            },
          }}
        />
      </Suspense>
    </div>
  );
}

export default SqlEditor;
