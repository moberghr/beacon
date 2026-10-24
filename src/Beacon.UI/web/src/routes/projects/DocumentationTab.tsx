import { useState } from 'react';
import { AlertTriangle, BookOpen } from 'lucide-react';
import { Button, CardBody } from '@/components/beacon';
import { EmptyState } from '@/components/data/EmptyState';
import { formatNumber, formatRelativeTime, formatDateTime } from '@/lib/format';
import { useImportedDocumentsQuery, useProjectDocumentationQuery, type ProjectDocSectionEntry } from './queries';
import { EditDocumentationSectionDialog } from './EditDocumentationSectionDialog';
import { DocSectionContent } from './DocSectionContent';
import { ImportedDocumentsSection } from './ImportedDocumentsSection';

export default function DocumentationTab({ projectId, canCurateHere }: { projectId: number; canCurateHere: boolean }) {
  const { data, isLoading, isError, error } = useProjectDocumentationQuery(projectId);
  // The documents the host imported (ExposeDocs) are the project's documentation too: the MCP gateway's only kind.
  const imported = useImportedDocumentsQuery(projectId);
  const [editing, setEditing] = useState<ProjectDocSectionEntry | null>(null);

  if (isLoading || imported.isLoading) {
    return <CardBody><span className="text-text-muted">Loading documentation…</span></CardBody>;
  }

  const importedDocuments = imported.data ?? [];
  const importedSection = (imported.isError || importedDocuments.length > 0) && (
    <ImportedDocumentsSection projectId={projectId} documents={importedDocuments} isError={imported.isError} />
  );

  // A failed load of the generated documentation does not hide the imported documents.
  if (isError) {
    return (
      <CardBody>
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load documentation"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
        {importedSection}
      </CardBody>
    );
  }

  const latest = data?.latest;
  if (!latest) {
    return (
      <CardBody>
        {importedSection || (
          <EmptyState
            icon={<BookOpen />}
            title="No documentation generated yet"
            description="Documentation will appear here once generated for this project."
          />
        )}
      </CardBody>
    );
  }

  const sections = [...latest.sections].sort((a, b) => a.sortOrder - b.sortOrder);
  // Editing a section or regenerating the documentation changes what every caller's prompts are grounded on: a project
  // admin's (or an Admin's) to do.
  const canCurate = canCurateHere && data?.canCurate === true;

  return (
    <>
      <CardBody>
        <div className="text-text-muted text-xs mb-4">
          Generated <strong className="text-text">{formatRelativeTime(latest.generatedAt)}</strong>
          {' '}by <span className="mono">{latest.generatedByModel}</span>
          {' · '}
          {formatNumber(latest.tablesAnalyzed)} tables · {formatNumber(latest.codeReferencesAnalyzed)} code refs
          {' · '}
          {formatNumber(latest.inputTokens + latest.outputTokens)} tokens
          {' · '}
          {formatDateTime(latest.generatedAt)}
        </div>
        {!canCurate && (
          <p className="text-text-muted text-xs mb-4">
            Only a project admin of this project (or an Admin) edits or regenerates its documentation.
          </p>
        )}

        <div className="flex flex-col gap-6">
          {sections.map(section => (
            <section key={section.id}>
              <header className="flex items-center justify-between mb-2">
                <h3 className="m-0 text-sm font-semibold text-text">{section.title}</h3>
                {canCurate && (
                  <Button
                    variant="ghost"
                    onClick={() => setEditing(section)}
                    aria-label={`Edit ${section.title}`}
                  >
                    Edit
                  </Button>
                )}
              </header>
              <DocSectionContent content={section.content} />
            </section>
          ))}
          {importedSection}
        </div>
      </CardBody>

      <EditDocumentationSectionDialog
        open={editing !== null}
        section={editing}
        projectId={projectId}
        onClose={() => setEditing(null)}
      />
    </>
  );
}
