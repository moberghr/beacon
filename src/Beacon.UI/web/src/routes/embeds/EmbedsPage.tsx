import { useMemo, useState } from 'react';
import { toast } from 'sonner';
import { AlertTriangle, AppWindow, Plus, RefreshCw } from 'lucide-react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { Banner, Button, Card, PageHeader, Pill } from '@/components/beacon';
import { formatDateTime, formatNumber } from '@/lib/format';
import { CreateEmbedModal } from './CreateEmbedModal';
import { useEmbedGrantsList, useRevokeEmbedGrant, type EmbedGrantEntry, type EmbedGrantStatus } from './queries';

const GRID_TEMPLATE = '1.2fr 1fr 1.2fr 1.3fr 0.9fr 0.9fr 0.9fr 0.8fr 80px';

const STATUS_TONE: Record<EmbedGrantStatus, 'ok' | 'warn' | 'neutral' | 'crit'> = {
  active: 'ok',
  expired: 'neutral',
  superseded: 'warn',
  revoked: 'crit',
};

const STATUS_LABEL: Record<EmbedGrantStatus, string> = {
  active: 'Active',
  expired: 'Expired',
  superseded: 'Superseded',
  revoked: 'Revoked',
};

/** Embed grants (SecurityAdmin): read-only views of approved queries that other sites may frame. */
export default function EmbedsPage() {
  const list = useEmbedGrantsList();
  const { data, isLoading, isError, error, refetch } = list;
  const revoke = useRevokeEmbedGrant();
  const [createOpen, setCreateOpen] = useState(false);
  const [revoking, setRevoking] = useState<EmbedGrantEntry | null>(null);

  const columns = useMemo<Column<EmbedGrantEntry>[]>(
    () => [
      {
        key: 'name',
        sortKey: 'name',
        header: 'Name',
        render: x => <span className="font-semibold text-text">{x.name}</span>,
      },
      {
        key: 'project',
        header: 'Project',
        render: x => <span className="text-text-muted">{x.projectName ?? `Project ${x.projectId}`}</span>,
      },
      {
        key: 'query',
        header: 'Query',
        render: x => (
          <span className="text-text-muted">
            {x.queryName ?? `Query ${x.queryId}`} <span className="text-text-subtle">v{x.versionNumber}</span>
          </span>
        ),
      },
      {
        key: 'origins',
        header: 'Origins',
        render: x => (
          <span className="flex flex-col">
            {x.allowedOrigins.map(y => (
              <code key={y} className="text-xs">
                {y}
              </code>
            ))}
          </span>
        ),
      },
      {
        key: 'createdBy',
        header: 'Created by',
        render: x => <span className="text-text-muted">{x.createdByLabel ?? '—'}</span>,
      },
      {
        key: 'expires',
        sortKey: 'expiresTime',
        header: 'Expires',
        render: x => <span className="text-text-muted">{formatDateTime(x.expiresTime)}</span>,
      },
      {
        key: 'lastIssued',
        sortKey: 'lastTokenIssuedTime',
        header: 'Last issued',
        render: x => (
          <span className="text-text-muted">{x.lastTokenIssuedTime ? formatDateTime(x.lastTokenIssuedTime) : 'Never'}</span>
        ),
      },
      {
        key: 'status',
        header: 'Status',
        render: x => (
          <Pill tone={STATUS_TONE[x.status] ?? 'neutral'} dot>
            {STATUS_LABEL[x.status] ?? x.status}
          </Pill>
        ),
      },
      {
        key: 'actions',
        header: '',
        render: x =>
          x.status === 'active' || x.status === 'superseded' ? (
            <Button type="button" variant="danger" size="sm" onClick={() => setRevoking(x)}>
              Revoke
            </Button>
          ) : null,
      },
    ],
    [],
  );

  const onConfirmRevoke = async () => {
    if (revoking == null) return;
    try {
      await revoke.mutateAsync(revoking.id);
      toast.success(`Revoked '${revoking.name}'`);
      setRevoking(null);
    } catch {
      // The mutation already toasted the error; the dialog stays open for a retry.
    }
  };

  return (
    <div className="flex flex-col gap-3 p-7">
      <PageHeader
        eyebrow="Access"
        prefix="Embedded"
        emphasis="views"
        sub={
          isLoading ? (
            <span className="text-text-muted">Loading…</span>
          ) : (
            <span className="text-text-muted">{formatNumber(data?.totalCount ?? 0)} grants</span>
          )
        }
        actions={
          <>
            <Button icon={<RefreshCw />} type="button" onClick={() => refetch()} disabled={isLoading}>
              Refresh
            </Button>
            <Button variant="primary" icon={<Plus />} type="button" onClick={() => setCreateOpen(true)}>
              New grant
            </Button>
          </>
        }
      />

      <Banner
        tone="info"
        icon={<AppWindow />}
        title="An embed grant shows one approved query to anyone its framing site shows it to."
        sub="Rows are always PII-masked and capped. Revoking a grant stops every outstanding token at once."
      />

      {isError ? (
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load embed grants"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      ) : (
        <Card>
          <DataTable
            columns={columns}
            rows={list.items}
            {...list.tableProps}
            rowKey={x => x.id}
            gridTemplate={GRID_TEMPLATE}
            empty={
              <EmptyState
                icon={<AppWindow />}
                title={isLoading ? 'Loading embed grants…' : 'No embed grants yet'}
                description={isLoading ? '' : 'Create a grant to let another site frame a read-only view of a query.'}
              />
            }
          />
        </Card>
      )}

      <CreateEmbedModal open={createOpen} onClose={() => setCreateOpen(false)} />

      <ConfirmDialog
        open={revoking != null}
        title="Revoke embed grant"
        message={
          revoking ? (
            <>
              Revoke <strong>{revoking.name}</strong>? Every page framing it stops showing the view immediately. A revoked
              grant cannot be restored.
            </>
          ) : (
            ''
          )
        }
        confirmLabel="Revoke grant"
        destructive
        busy={revoke.isPending}
        onConfirm={onConfirmRevoke}
        onCancel={() => setRevoking(null)}
      />
    </div>
  );
}
