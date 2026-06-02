import { useRef } from 'react';
import { SqlEditor, type MonacoEditorLike } from '@/components/ui/SqlEditor';
import { DatabaseExplorer } from '@/components/ui/DatabaseExplorer';
import { useDataSourceMetadataQuery } from '../../data-sources/queries';

export interface NewStepEditorWithExplorerProps {
  draftId: number;
  dataSourceId: number;
  sqlValue: string;
  onSqlChange: (sql: string) => void;
  parameterNames: string[];
  crossStepResultCount: number;
}

/**
 * SQL editor + schema explorer pair used inside the multi-step
 * "create query" flow. Owns its own Monaco ref so the explorer can
 * insert text at the editor cursor. Schema metadata is fetched lazily
 * per data source (TanStack Query handles dedupe + caching).
 */
export function NewStepEditorWithExplorer({
  draftId,
  dataSourceId,
  sqlValue,
  onSqlChange,
  parameterNames,
  crossStepResultCount,
}: NewStepEditorWithExplorerProps) {
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
          id={`new-step-${draftId}-sql`}
          height={280}
          value={sqlValue}
          onChange={onSqlChange}
          metadata={metadataQuery.data ?? null}
          parameterNames={parameterNames}
          crossStepResultCount={crossStepResultCount}
          onEditorReady={(editor) => {
            editorRef.current = editor;
          }}
        />
      </div>
    </div>
  );
}
