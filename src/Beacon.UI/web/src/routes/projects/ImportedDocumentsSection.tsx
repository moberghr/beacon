import { useState } from 'react';
import { ChevronDown } from 'lucide-react';
import { cn } from '@/lib/utils';
import { formatRelativeTime } from '@/lib/format';
import { DocSectionContent } from './DocSectionContent';
import { useImportedDocumentQuery, type ImportedDocumentSummaryEntry } from './queries';

interface ImportedDocumentsSectionProps {
  projectId: number;
  documents: ImportedDocumentSummaryEntry[];
  /** The list could not be loaded: said here, without hiding the generated documentation next to it. */
  isError: boolean;
}

/**
 * The documents the host application imported into the project (`ExposeDocs`), which agents read through the
 * `get_documentation` tool. Each opens on demand: its content is read only then.
 */
export function ImportedDocumentsSection({ projectId, documents, isError }: ImportedDocumentsSectionProps) {
  const [openId, setOpenId] = useState<number | null>(null);

  return (
    <section>
      <h3 className="m-0 mb-1 text-sm font-semibold text-text">Imported documents</h3>
      <p className="text-text-muted text-xs mb-3">
        Shipped by the host application; agents read them through the <span className="mono">get_documentation</span> tool.
      </p>
      {isError ? (
        <p className="m-0 text-xs text-crit">The imported documents could not be loaded.</p>
      ) : (
        <ul className="m-0 p-0 list-none flex flex-col gap-2">
          {documents.map(x => {
            const open = openId === x.id;
            return (
              <li key={x.id} className="border border-border rounded-md">
                <button
                  type="button"
                  aria-expanded={open}
                  onClick={() => setOpenId(open ? null : x.id)}
                  className="w-full flex items-center gap-3 px-3 py-2 text-left hover:bg-surface-2"
                >
                  <span className="flex-1 min-w-0">
                    <span className="block text-sm font-medium text-text truncate">{x.title}</span>
                    <span className="block mono text-2xs text-text-subtle truncate">{x.path}</span>
                  </span>
                  <span className="text-2xs text-text-muted shrink-0">Imported {formatRelativeTime(x.importedTime)}</span>
                  <ChevronDown className={cn('size-3.5 shrink-0 text-text-subtle transition-transform', open && 'rotate-180')} />
                </button>
                {open && (
                  <div className="border-t border-border px-3 py-3">
                    <ImportedDocumentBody projectId={projectId} documentId={x.id} />
                  </div>
                )}
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}

function ImportedDocumentBody({ projectId, documentId }: { projectId: number; documentId: number }) {
  const { data, isLoading, isError } = useImportedDocumentQuery(projectId, documentId);

  if (isLoading) {
    return <span className="text-text-muted text-xs">Loading document…</span>;
  }

  if (isError || !data) {
    return <span className="text-xs text-crit">The document could not be loaded.</span>;
  }

  return <DocSectionContent content={data.content} />;
}
