import { useMemo, useState } from 'react';
import { toast } from 'sonner';
import { AlertTriangle, FolderTree, Plus, RefreshCw, X } from 'lucide-react';
import { Button, PageHeader } from '@/components/beacon';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { formatNumber } from '@/lib/format';
import { useSearchFilter } from '@/lib/usePagedList';
import { useAccess } from '@/auth/useAuth';
import { useDeleteQueryFolder, useQueryFoldersList, type QueryFolderEntry } from './queries';
import { QueryFolderDialog } from './QueryFolderDialog';

const GRID_TEMPLATE = '1.6fr 1.6fr 0.6fr 0.6fr 60px';

export default function QueryFoldersPage() {
  const list = useQueryFoldersList();
  const { data, isLoading, isError, error, refetch } = list;
  const deleteMutation = useDeleteQueryFolder();
  const canWrite = useAccess().canWrite;

  const [editing, setEditing] = useState<QueryFolderEntry | null | undefined>(undefined); // undefined = closed
  const [deleting, setDeleting] = useState<QueryFolderEntry | null>(null);
  const [moveToParent, setMoveToParent] = useState(true);
  const [search, setSearch] = useSearchFilter(list.filters.search, value => list.setFilter('search', value));

  const columns = useMemo<Column<QueryFolderEntry>[]>(() => [
    {
      key: 'name',
      header: 'Folder',
      sortKey: 'name',
      render: f => (
        <span className="flex flex-col gap-0.5">
          <span className="font-semibold text-text">{f.name}</span>
          <span className="mono text-xs text-text-muted">{f.path}</span>
        </span>
      ),
    },
    {
      key: 'description',
      header: 'Description',
      render: f => <span className="text-text-muted">{f.description || '—'}</span>,
    },
    { key: 'queries', header: 'Queries', render: f => formatNumber(f.queryCount) },
    { key: 'children', header: 'Subfolders', render: f => formatNumber(f.childFolderCount) },
    {
      key: 'actions',
      header: '',
      render: f => canWrite
        ? (
          <Button
            variant="ghost"
            aria-label={`Delete ${f.name}`}
            title="Delete folder"
            icon={<X />}
            onClick={e => { e.stopPropagation(); setMoveToParent(true); setDeleting(f); }}
          />
        )
        : null,
    },
  ], [canWrite]);

  const onConfirmDelete = async () => {
    if (deleting == null) return;
    try {
      await deleteMutation.mutateAsync({ id: deleting.id, moveQueriesToParent: moveToParent });
      toast.success(`Deleted folder '${deleting.name}'`);
      setDeleting(null);
    } catch {
      // useDeleteQueryFolder (createSimpleMutation) already toasts the error.
    }
  };

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        eyebrow="Alerts"
        emphasis="Query folders"
        sub={
          isLoading
            ? <span className="text-text-muted">Loading…</span>
            : <span className="text-text-muted">{formatNumber(data?.totalCount ?? 0)} total</span>
        }
        actions={
          <>
            <Button onClick={() => refetch()} disabled={isLoading} icon={<RefreshCw />}>Refresh</Button>
            {canWrite && (
              <Button variant="primary" onClick={() => setEditing(null)} icon={<Plus />}>Add folder</Button>
            )}
          </>
        }
      />

      {isError && (
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load folders"
          description={error instanceof Error ? error.message : 'Unknown error'}
          action={<Button variant="primary" onClick={() => refetch()}>Retry</Button>}
        />
      )}

      {!isError && (
        <>
          <div className="bg-surface border border-border rounded-md shadow-sm p-3">
            <input
              type="search"
              aria-label="Search folders"
              className="w-full bg-surface text-text border border-border-strong rounded-sm px-2.5 py-1.5 text-sm placeholder:text-text-subtle focus:border-brand-500 focus:outline-none focus:shadow-ring"
              placeholder="Search by name or path"
              value={search}
              onChange={e => setSearch(e.target.value)}
            />
          </div>

          <DataTable
            columns={columns}
            rows={list.items}
            {...list.tableProps}
            rowKey={f => f.id}
            gridTemplate={GRID_TEMPLATE}
            onRowClick={canWrite ? f => setEditing(f) : undefined}
            empty={
              <EmptyState
                icon={<FolderTree />}
                title={isLoading ? 'Loading folders…' : 'No folders yet'}
                description={isLoading ? '' : 'Add a folder to group saved queries; move queries into it from the Queries page.'}
              />
            }
          />
        </>
      )}

      <QueryFolderDialog
        open={editing !== undefined}
        folder={editing ?? null}
        onClose={() => setEditing(undefined)}
      />

      <ConfirmDialog
        open={deleting != null}
        title="Delete folder"
        message={
          deleting
            ? (
              <div className="flex flex-col gap-3">
                <span>Delete folder <strong>{deleting.name}</strong>? This cannot be undone.</span>
                {deleting.childFolderCount > 0 && (
                  <span className="text-xs text-text-muted">A folder with subfolders cannot be deleted; delete or move the subfolders first.</span>
                )}
                {deleting.queryCount > 0 && (
                  <label className="flex items-start gap-2 text-sm">
                    <input
                      type="checkbox"
                      className="mt-0.5"
                      checked={moveToParent}
                      onChange={e => setMoveToParent(e.target.checked)}
                    />
                    <span>
                      Move its queries to the parent folder. Unchecked, the folder cannot be deleted while it
                      still holds queries.
                    </span>
                  </label>
                )}
              </div>
            )
            : ''
        }
        confirmLabel="Delete"
        destructive
        busy={deleteMutation.isPending}
        onConfirm={onConfirmDelete}
        onCancel={() => setDeleting(null)}
      />
    </div>
  );
}
