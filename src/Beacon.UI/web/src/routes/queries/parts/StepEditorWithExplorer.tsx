import { useRef } from 'react';
import { SqlEditor, type MonacoEditorLike } from '@/components/ui/SqlEditor';
import { DatabaseExplorer } from '@/components/ui/DatabaseExplorer';
import { useDataSourceMetadataQuery } from '@/routes/data-sources/queries';

export interface StepEditorWithExplorerProps {
  /** Unique DOM id for the underlying Monaco editor instance. */
  editorId: string;
  dataSourceId: number;
  sqlValue: string;
  onSqlChange: (sql: string) => void;
  parameterNames: string[];
  crossStepResultCount: number;
  height?: number;
}

/**
 * SQL editor + schema explorer pair shared by the query editor and the
 * "create query" flow. Owns its own Monaco ref so the explorer can insert
 * text at the editor cursor. Schema metadata is fetched lazily per data
 * source (TanStack Query handles dedupe + caching).
 */
export function StepEditorWithExplorer({
  editorId,
  dataSourceId,
  sqlValue,
  onSqlChange,
  parameterNames,
  crossStepResultCount,
  height = 320,
}: StepEditorWithExplorerProps) {
  const editorRef = useRef<MonacoEditorLike | null>(null);
  const metadataQuery = useDataSourceMetadataQuery(dataSourceId > 0 ? dataSourceId : null);

  const insertAtCursor = (text: string) => {
    const editor = editorRef.current;
    if (!editor) return;
    const position = editor.getPosition();
    if (!position) return;
    editor.executeEdits('database-explorer', [
      {
        range: {
          startLineNumber: position.lineNumber,
          startColumn: position.column,
          endLineNumber: position.lineNumber,
          endColumn: position.column,
        },
        text,
        forceMoveMarkers: true,
      },
    ]);
    editor.focus();
  };

  return (
    <div className="grid gap-0 grid-cols-1 lg:grid-cols-[280px_minmax(0,1fr)]">
      <DatabaseExplorer dataSourceId={dataSourceId} onInsert={insertAtCursor} />
      <div className="min-w-0 flex flex-col">
        <SqlEditor
          id={editorId}
          height={height}
          value={sqlValue}
          onChange={onSqlChange}
          metadata={metadataQuery.data ?? null}
          parameterNames={parameterNames}
          crossStepResultCount={crossStepResultCount}
          onEditorReady={editor => {
            editorRef.current = editor;
          }}
        />
      </div>
    </div>
  );
}
