import { useMemo, useState } from 'react';
import { toast } from 'sonner';
import { Icon } from '@/components/Icon';
import { PageHeader } from '@/components/layout/PageHeader';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { ApiError } from '@/lib/api';
import { formatDateTime, formatNumber } from '@/lib/format';
import { useApiKeysQuery, useRevokeApiKey, type ApiKeyEntry } from './queries';
import { GenerateApiKeyDialog } from './GenerateApiKeyDialog';

const GRID_TEMPLATE = '1.4fr 0.9fr 1.2fr 1fr 1fr 0.9fr 0.7fr 80px';

function scopePillClass(scope: string): string {
  switch (scope) {
    case 'Admin': return 'pill pill--crit';
    case 'Execute': return 'pill pill--warn';
    case 'Read': return 'pill pill--info';
    default: return 'pill';
  }
}

export default function ApiKeysListPage() {
  const { data, isLoading, isError, error, refetch } = useApiKeysQuery();
  const revoke = useRevokeApiKey();
  const [generateOpen, setGenerateOpen] = useState(false);
  const [revoking, setRevoking] = useState<ApiKeyEntry | null>(null);

  const entries = data?.entries ?? [];

  const columns = useMemo<Column<ApiKeyEntry>[]>(() => [
    {
      key: 'name',
      header: 'Name',
      render: k => <span style={{ fontWeight: 600, color: 'var(--text)' }}>{k.name}</span>,
    },
    {
      key: 'prefix',
      header: 'Prefix',
      render: k => <code style={{ fontSize: 12 }}>{k.prefix}…</code>,
    },
    {
      key: 'scopes',
      header: 'Scopes',
      render: k => (
        <span style={{ display: 'inline-flex', gap: 4, flexWrap: 'wrap' }}>
          {k.scopes.map(s => <span key={s} className={scopePillClass(s)}>{s}</span>)}
        </span>
      ),
    },
    {
      key: 'created',
      header: 'Created',
      render: k => <span className="muted">{formatDateTime(k.createdAt as unknown as string)}</span>,
    },
    {
      key: 'lastUsed',
      header: 'Last used',
      render: k => k.lastUsedAt
        ? <span className="muted">{formatDateTime(k.lastUsedAt as unknown as string)}</span>
        : <span className="muted">Never</span>,
    },
    {
      key: 'expires',
      header: 'Expires',
      render: k => {
        if (!k.expiresAt) return <span className="muted">Never</span>;
        const date = new Date(k.expiresAt as unknown as string);
        const expired = !Number.isNaN(date.getTime()) && date < new Date();
        return <span className={expired ? 'pill pill--crit' : 'pill'}>{expired ? 'Expired' : formatDateTime(k.expiresAt as unknown as string)}</span>;
      },
    },
    {
      key: 'status',
      header: 'Status',
      render: k => k.isActive
        ? <span className="pill pill--ok">Active</span>
        : <span className="pill pill--crit">Revoked</span>,
    },
    {
      key: 'actions',
      header: '',
      render: k => k.isActive
        ? (
          <button
            type="button"
            className="btn btn--danger"
            onClick={() => setRevoking(k)}
          >
            Revoke
          </button>
        )
        : null,
    },
  ], []);

  const onConfirmRevoke = async () => {
    if (revoking == null) return;
    try {
      await revoke.mutateAsync(revoking.id);
      toast.success(`Revoked '${revoking.name}'`);
      setRevoking(null);
    } catch (err) {
      const message = err instanceof ApiError
        ? err.body || `Revoke failed (${err.status})`
        : err instanceof Error ? err.message : 'Unknown error';
      toast.error(message);
    }
  };

  return (
    <div className="page">
      <PageHeader
        title="API keys"
        sub={
          isLoading
            ? <span className="muted">Loading…</span>
            : <span className="muted">{formatNumber(entries.length)} total</span>
        }
        actions={
          <>
            <button className="btn" type="button" onClick={() => refetch()} disabled={isLoading}>
              <Icon.Refresh size={14} className="btn__icon" />
              Refresh
            </button>
            <button className="btn btn--primary" type="button" onClick={() => setGenerateOpen(true)}>
              <Icon.Key size={14} className="btn__icon" />
              Generate key
            </button>
          </>
        }
      />

      <div className="empty-state" style={{ marginBottom: 12, borderColor: 'var(--warn)', background: 'var(--warn-bg)' }}>
        <div className="empty-state__icon" style={{ color: 'var(--warn)' }}>
          <Icon.Shield size={20} />
        </div>
        <div>
          <div className="empty-state__title">Treat API keys like passwords.</div>
          <div className="empty-state__sub">Rotate them regularly. Revoke any key that may have been exposed.</div>
        </div>
      </div>

      {isError && (
        <EmptyState
          icon={<Icon.Alert size={20} />}
          title="Failed to load API keys"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      )}

      {!isError && (
        <div className="card" style={{ padding: 0 }}>
          <DataTable
            columns={columns}
            rows={entries}
            rowKey={k => k.id}
            gridTemplate={GRID_TEMPLATE}
            empty={
              <EmptyState
                icon={<Icon.Key size={20} />}
                title={isLoading ? 'Loading API keys…' : 'No API keys yet'}
                description={isLoading ? '' : 'Generate a key to enable programmatic access to Beacon.'}
                action={
                  isLoading ? null : (
                    <button className="btn btn--primary" type="button" onClick={() => setGenerateOpen(true)}>
                      Generate first key
                    </button>
                  )
                }
              />
            }
          />
        </div>
      )}

      <GenerateApiKeyDialog open={generateOpen} onClose={() => setGenerateOpen(false)} />

      <ConfirmDialog
        open={revoking != null}
        title="Revoke API key"
        message={
          revoking
            ? <>Revoke <strong>{revoking.name}</strong>? Integrations using this key will stop working immediately.</>
            : ''
        }
        confirmLabel="Revoke key"
        destructive
        busy={revoke.isPending}
        onConfirm={onConfirmRevoke}
        onCancel={() => setRevoking(null)}
      />
    </div>
  );
}
