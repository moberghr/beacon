import { useMemo, useState } from 'react';
import { toast } from 'sonner';
import { AlertTriangle, Plus, RefreshCw, Users, X } from 'lucide-react';
import { Button, PageHeader, Pill } from '@/components/beacon';
import { useSearchFilter } from '@/lib/usePagedList';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { formatNumber } from '@/lib/format';
import { useAccess } from '@/auth/useAuth';
import {
  NOTIFICATION_TYPE_LABEL,
  useDeleteRecipient,
  useRecipientsList,
  type RecipientEntry,
} from './queries';
import { RecipientDialog } from './RecipientDialog';
import { AwaitingApprovalPill, RejectedChangeNote } from '../approvals/AwaitingApproval';
import { ChangeApprovalKind } from '@/lib/enums';

const GRID_TEMPLATE = '0.6fr 1.4fr 1.6fr 0.9fr 1.6fr 0.6fr 60px';

export default function RecipientsListPage() {
  const list = useRecipientsList();
  const { data, isLoading, isError, error, refetch } = list;
  const deleteMutation = useDeleteRecipient();
  // Only a SecurityAdmin (or an Admin) creates, edits or deletes recipients (the server enforces it); others list names
  // and types.
  const canManage = useAccess().hasPermission('SecurityAdmin');

  const [editing, setEditing] = useState<RecipientEntry | null | undefined>(undefined); // undefined = closed
  const [deleting, setDeleting] = useState<RecipientEntry | null>(null);
  const [search, setSearch] = useSearchFilter(list.filters.search, value => list.setFilter('search', value));

  const columns = useMemo<Column<RecipientEntry>[]>(() => [
    { key: 'id', header: 'Id', sortKey: 'id', render: r => <span className="text-text-muted mono">{r.id}</span> },
    {
      key: 'name',
      sortKey: 'name',
      header: 'Name',
      render: r => (
        <span className="flex flex-col gap-0.5">
          <span className="flex items-center gap-2">
            <span className="font-semibold text-text">{r.name}</span>
            {r.awaitingApproval && (
              <AwaitingApprovalPill
                kind={ChangeApprovalKind.RecipientDestination}
                requestId={r.pendingApprovalRequestId}
                lastDecision={r.lastApprovalDecision}
              />
            )}
          </span>
          {r.awaitingApproval && (
            <RejectedChangeNote
              kind={ChangeApprovalKind.RecipientDestination}
              requestId={r.pendingApprovalRequestId}
              lastDecision={r.lastApprovalDecision}
            />
          )}
        </span>
      ),
    },
    {
      key: 'destination',
      header: 'Destination',
      render: r => r.secretsUnreadable
        ? <Pill tone="crit" title="The stored destination or headers cannot be read. Open the recipient and enter them again.">Re-enter destination</Pill>
        : r.destination
          ? (
            <span className="flex flex-col gap-0.5">
              <span className="mono text-xs">{r.destination}</span>
              <Fingerprints recipient={r} />
            </span>
          )
          : <span className="text-text-muted">—</span>,
    },
    {
      key: 'type',
      sortKey: 'notificationType',
      header: 'Type',
      render: r => <Pill>{NOTIFICATION_TYPE_LABEL[r.notificationType] ?? r.notificationType}</Pill>,
    },
    {
      key: 'description',
      header: 'Description',
      render: r => r.description
        ? <span className="text-text-muted">{r.description}</span>
        : <span className="text-text-muted">—</span>,
    },
    {
      key: 'subs',
      sortKey: 'subscriptionCount',
      header: 'Subs',
      render: r => formatNumber(r.subscriptionCount),
    },
    {
      key: 'actions',
      header: '',
      render: r => canManage
        ? (
          <Button
            variant="ghost"
            aria-label={`Delete ${r.name}`}
            onClick={e => { e.stopPropagation(); setDeleting(r); }}
            title="Delete recipient"
            icon={<X />}
          />
        )
        : null,
    },
  ], [canManage]);

  const onConfirmDelete = async () => {
    if (deleting == null) return;
    try {
      await deleteMutation.mutateAsync(deleting.id);
      toast.success(`Deleted recipient '${deleting.name}'`);
      setDeleting(null);
    } catch {
      // useDeleteRecipient (createSimpleMutation) already toasts the error.
    }
  };

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        eyebrow="Notifications"
        emphasis="Recipients"
        sub={
          isLoading
            ? <span className="text-text-muted">Loading…</span>
            : <span className="text-text-muted">{formatNumber(data?.totalCount ?? 0)} total</span>
        }
        actions={
          <>
            <Button onClick={() => refetch()} disabled={isLoading} icon={<RefreshCw />}>
              Refresh
            </Button>
            {canManage && (
              <Button variant="primary" onClick={() => setEditing(null)} icon={<Plus />}>
                Add recipient
              </Button>
            )}
          </>
        }
      />

      {isError && (
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load recipients"
          description={error instanceof Error ? error.message : 'Unknown error'}
          action={
            <Button variant="primary" onClick={() => refetch()}>
              Retry
            </Button>
          }
        />
      )}

      {!isError && (
        <>
          <div className="bg-surface border border-border rounded-md shadow-sm p-3">
            <input
              type="search"
              className="w-full bg-surface text-text border border-border-strong rounded-sm px-2.5 py-1.5 text-sm placeholder:text-text-subtle focus:border-brand-500 focus:outline-none focus:shadow-ring"
              placeholder="Search by name or description"
              value={search}
              onChange={e => setSearch(e.target.value)}
            />
          </div>

          <DataTable
            columns={columns}
            rows={list.items}
            {...list.tableProps}
            rowKey={r => r.id}
            gridTemplate={GRID_TEMPLATE}
            onRowClick={canManage ? r => setEditing(r) : undefined}
            empty={
              <EmptyState
                icon={<Users />}
                title={isLoading ? 'Loading recipients…' : 'No recipients yet'}
                description={isLoading
                  ? ''
                  : canManage
                    ? 'Add a recipient so subscriptions have somewhere to send alerts.'
                    : 'An Admin or a SecurityAdmin adds recipients; you can then attach them to subscriptions.'}
              />
            }
          />
        </>
      )}

      <RecipientDialog
        open={editing !== undefined}
        recipient={editing ?? null}
        onClose={() => setEditing(undefined)}
      />

      <ConfirmDialog
        open={deleting != null}
        title="Delete recipient"
        message={
          deleting
            ? <>Delete recipient <strong>{deleting.name}</strong>? This cannot be undone.</>
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

/**
 * Shown to those who change recipients: short keyed fingerprints of where the recipient delivers and of each header
 * value, the same an Approver is shown, so the two can compare them out of band. Equal fingerprints mean equal values.
 */
function Fingerprints({ recipient }: { recipient: RecipientEntry }) {
  if (!recipient.definitionFingerprint) {
    return null;
  }

  return (
    <span className="text-2xs text-text-muted" aria-label={`Fingerprints of ${recipient.name}`}>
      Fingerprint <span className="mono">{recipient.definitionFingerprint}</span>
      {(recipient.headerFingerprints ?? []).map(x => (
        <span key={x.name}>
          {' · '}
          {x.name} <span className="mono">{x.fingerprint}</span>
        </span>
      ))}
    </span>
  );
}
