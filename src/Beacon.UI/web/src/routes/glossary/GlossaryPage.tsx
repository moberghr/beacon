import { useMemo, useState } from 'react';
import { toast } from 'sonner';
import { AlertTriangle, BookOpen, Plus, RefreshCw, X } from 'lucide-react';
import { Button, PageHeader, Pill } from '@/components/beacon';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { formatNumber } from '@/lib/format';
import { useSearchFilter } from '@/lib/usePagedList';
import { useAccess } from '@/auth/useAuth';
import { ProjectPicker } from '@/routes/projects/ProjectPicker';
import { useDeleteGlossaryTerm, useGlossaryList, type GlossaryTermEntry } from './queries';
import { GlossaryTermDialog } from './GlossaryTermDialog';

const GRID_TEMPLATE = '1fr 2fr 1.2fr 0.6fr 60px';

function targetOf(t: GlossaryTermEntry): string | null {
  const parts = [t.targetSchema, t.targetTable, t.targetColumn].filter(x => x);
  return parts.length > 0 ? parts.join('.') : null;
}

/** The business glossary of one project. Admin-only (the server enforces it; the menu hides it from others). */
export default function GlossaryPage() {
  const list = useGlossaryList();
  const { data, isLoading, isError, error, refetch } = list;
  const deleteMutation = useDeleteGlossaryTerm();
  const isAdmin = useAccess().isAdmin;

  const projectId = list.filters.projectId === '' ? null : Number(list.filters.projectId);
  const [editing, setEditing] = useState<GlossaryTermEntry | null | undefined>(undefined); // undefined = closed
  const [deleting, setDeleting] = useState<GlossaryTermEntry | null>(null);
  const [search, setSearch] = useSearchFilter(list.filters.search, value => list.setFilter('search', value));

  const columns = useMemo<Column<GlossaryTermEntry>[]>(() => [
    {
      key: 'term',
      header: 'Term',
      sortKey: 'term',
      render: t => (
        <span className="flex flex-col gap-0.5">
          <span className="flex items-center gap-2">
            <span className="font-semibold text-text">{t.term}</span>
            {!t.isActive && <Pill tone="warn">Inactive</Pill>}
          </span>
          {t.synonyms && <span className="text-xs text-text-muted">{t.synonyms}</span>}
        </span>
      ),
    },
    { key: 'definition', header: 'Definition', render: t => <span className="text-text-muted">{t.definition}</span> },
    {
      key: 'target',
      header: 'Target',
      render: t => {
        const target = targetOf(t);
        return target ? <span className="mono text-xs">{target}</span> : <span className="text-text-muted">—</span>;
      },
    },
    { key: 'source', header: 'Source', render: t => (t.dataSourceId == null ? <span className="text-text-muted">All</span> : `#${formatNumber(t.dataSourceId)}`) },
    {
      key: 'actions',
      header: '',
      render: t => (
        <Button
          variant="ghost"
          aria-label={`Delete ${t.term}`}
          title="Delete term"
          icon={<X />}
          onClick={e => { e.stopPropagation(); setDeleting(t); }}
        />
      ),
    },
  ], []);

  const onConfirmDelete = async () => {
    if (deleting == null) return;
    try {
      await deleteMutation.mutateAsync(deleting.id);
      toast.success(`Deleted term '${deleting.term}'`);
      setDeleting(null);
    } catch {
      // useDeleteGlossaryTerm (createSimpleMutation) already toasts the error.
    }
  };

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        eyebrow="MCP"
        emphasis="Glossary"
        sub={
          projectId == null
            ? <span className="text-text-muted">Choose a project to see its terms</span>
            : isLoading
              ? <span className="text-text-muted">Loading…</span>
              : <span className="text-text-muted">{formatNumber(data?.totalCount ?? 0)} total</span>
        }
        actions={
          <>
            <Button onClick={() => refetch()} disabled={isLoading || projectId == null} icon={<RefreshCw />}>Refresh</Button>
            {isAdmin && projectId != null && (
              <Button variant="primary" onClick={() => setEditing(null)} icon={<Plus />}>Add term</Button>
            )}
          </>
        }
      />

      <div className="bg-surface border border-border rounded-md shadow-sm p-3 flex flex-wrap items-center gap-3">
        <ProjectPicker
          value={projectId}
          onSelect={p => list.setFilter('projectId', p ? String(p.id) : '')}
          className="w-64"
        />
        <input
          type="search"
          aria-label="Search terms"
          disabled={projectId == null}
          className="flex-1 min-w-48 bg-surface text-text border border-border-strong rounded-sm px-2.5 py-1.5 text-sm placeholder:text-text-subtle focus:border-brand-500 focus:outline-none focus:shadow-ring disabled:bg-surface-2"
          placeholder="Search by term"
          value={search}
          onChange={e => setSearch(e.target.value)}
        />
        <label className="flex items-center gap-2 text-sm text-text-muted">
          <input
            type="checkbox"
            disabled={projectId == null}
            checked={list.filters.inactive === 'true'}
            onChange={e => list.setFilter('inactive', e.target.checked ? 'true' : '')}
          />
          Include inactive
        </label>
      </div>

      {projectId == null && (
        <EmptyState
          icon={<BookOpen />}
          title="Choose a project"
          description="Glossary terms belong to one project. Pick a project to list, add or edit its terms."
        />
      )}

      {projectId != null && isError && (
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load glossary terms"
          description={error instanceof Error ? error.message : 'Unknown error'}
          action={<Button variant="primary" onClick={() => refetch()}>Retry</Button>}
        />
      )}

      {projectId != null && !isError && (
        <DataTable
          columns={columns}
          rows={list.items}
          {...list.tableProps}
          rowKey={t => t.id}
          gridTemplate={GRID_TEMPLATE}
          onRowClick={isAdmin ? t => setEditing(t) : undefined}
          empty={
            <EmptyState
              icon={<BookOpen />}
              title={isLoading ? 'Loading terms…' : 'No glossary terms'}
              description={isLoading ? '' : 'Add the business terms people use so Beacon can map them to your data.'}
            />
          }
        />
      )}

      {projectId != null && (
        <GlossaryTermDialog
          open={editing !== undefined}
          projectId={projectId}
          term={editing ?? null}
          onClose={() => setEditing(undefined)}
        />
      )}

      <ConfirmDialog
        open={deleting != null}
        title="Delete glossary term"
        message={deleting ? <>Delete term <strong>{deleting.term}</strong>? This cannot be undone.</> : ''}
        confirmLabel="Delete"
        destructive
        busy={deleteMutation.isPending}
        onConfirm={onConfirmDelete}
        onCancel={() => setDeleting(null)}
      />
    </div>
  );
}
