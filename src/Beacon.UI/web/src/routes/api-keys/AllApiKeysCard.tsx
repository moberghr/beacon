import { useMemo, useState } from 'react';
import { toast } from 'sonner';
import { AlertTriangle, Key, X } from 'lucide-react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { Button, Card, CardHead, CardTitle, CardSub, CardActions } from '@/components/beacon';
import { formatDateTime, formatNumber } from '@/lib/format';
import { useAdminRevokeApiKey, useAllApiKeysList, type AdminApiKeyEntry } from './queries';
import { ExpiresCell, ProjectsCell, ScopePills, StatusPill } from './columns';

const GRID_TEMPLATE = '1fr 1.2fr 0.9fr 1fr 0.8fr 1fr 0.9fr 0.7fr 80px';

/**
 * Administrators: every user's API keys with their owner, filterable to one user (click the owner), and revocable.
 * Rendered only for the Admin role; the endpoints refuse everyone else.
 */
export function AllApiKeysCard() {
  const list = useAllApiKeysList(true);
  const { data, isError, error, setFilter } = list;
  const revoke = useAdminRevokeApiKey();
  const [revoking, setRevoking] = useState<AdminApiKeyEntry | null>(null);
  const userFilter = list.filters.userId;

  const columns = useMemo<Column<AdminApiKeyEntry>[]>(() => [
    {
      key: 'owner',
      sortKey: 'userName',
      header: 'Owner',
      render: k => k.userId == null
        ? <span className="text-text-muted">—</span>
        : (
          <button
            type="button"
            className="text-left font-semibold text-text hover:underline"
            title="Show only this user's keys"
            onClick={() => setFilter('userId', String(k.userId))}
          >
            {k.userName ?? `#${k.userId}`}
          </button>
        ),
    },
    { key: 'name', sortKey: 'name', header: 'Name', render: k => <span className="text-text">{k.name}</span> },
    { key: 'prefix', header: 'Prefix', render: k => <code className="text-xs">{k.prefix}…</code> },
    { key: 'scopes', header: 'Scopes', render: k => <ScopePills scopes={k.scopes} /> },
    { key: 'projects', header: 'Projects', render: k => <ProjectsCell ids={k.allowedProjectIds} /> },
    {
      key: 'lastUsed',
      sortKey: 'lastUsedAt',
      header: 'Last used',
      render: k => <span className="text-text-muted">{k.lastUsedAt ? formatDateTime(k.lastUsedAt) : 'Never'}</span>,
    },
    { key: 'expires', sortKey: 'expiresAt', header: 'Expires', render: k => <ExpiresCell expiresAt={k.expiresAt} /> },
    { key: 'status', header: 'Status', render: k => <StatusPill isActive={k.isActive} revokedAt={k.revokedAt} /> },
    {
      key: 'actions',
      header: '',
      render: k => k.isActive
        ? <Button type="button" variant="danger" size="sm" onClick={() => setRevoking(k)}>Revoke</Button>
        : null,
    },
  ], [setFilter]);

  const onConfirmRevoke = async () => {
    if (revoking == null) return;
    try {
      await revoke.mutateAsync(revoking.id);
      toast.success(`Revoked '${revoking.name}'`);
      setRevoking(null);
    } catch {
      // createSimpleMutation already surfaced the error toast — keep the dialog open so the admin can retry.
    }
  };

  return (
    <Card>
      <CardHead>
        <CardTitle>All API keys</CardTitle>
        <CardSub>
          {userFilter ? 'one user · ' : 'every user · '}
          {formatNumber(data?.totalCount ?? 0)} total
        </CardSub>
        {userFilter && (
          <CardActions>
            <Button type="button" size="sm" icon={<X />} onClick={() => setFilter('userId', '')}>
              Show all users
            </Button>
          </CardActions>
        )}
      </CardHead>

      {isError
        ? (
          <EmptyState
            icon={<AlertTriangle />}
            title="Failed to load API keys"
            description={error instanceof Error ? error.message : 'Unknown error'}
          />
        )
        : (
          <DataTable
            columns={columns}
            rows={list.items}
            {...list.tableProps}
            rowKey={k => k.id}
            gridTemplate={GRID_TEMPLATE}
            empty={<EmptyState icon={<Key />} title={list.isLoading ? 'Loading API keys…' : 'No API keys'} description="" />}
          />
        )}

      <ConfirmDialog
        open={revoking != null}
        title="Revoke API key"
        message={
          revoking
            ? <>Revoke <strong>{revoking.name}</strong> of <strong>{revoking.userName ?? 'its owner'}</strong>? Integrations using this key will stop working immediately.</>
            : ''
        }
        confirmLabel="Revoke key"
        destructive
        busy={revoke.isPending}
        onConfirm={onConfirmRevoke}
        onCancel={() => setRevoking(null)}
      />
    </Card>
  );
}
