import { useMemo, useState } from 'react';
import { toast } from 'sonner';
import { RefreshCw, Key, Shield, AlertTriangle } from 'lucide-react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { Button, Pill, Card, Banner, PageHeader } from '@/components/beacon';
import { formatDateTime, formatNumber } from '@/lib/format';
import { useApiKeysQuery, useRevokeApiKey, type ApiKeyEntry } from './queries';
import { GenerateApiKeyDialog } from './GenerateApiKeyDialog';

const GRID_TEMPLATE = '1.4fr 0.9fr 1.2fr 1fr 1fr 0.9fr 0.7fr 80px';

function scopeTone(scope: string): 'crit' | 'warn' | 'info' | 'neutral' {
  switch (scope) {
    case 'Admin': return 'crit';
    case 'Execute': return 'warn';
    case 'Read': return 'info';
    default: return 'neutral';
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
      render: k => <span className="font-semibold text-text">{k.name}</span>,
    },
    {
      key: 'prefix',
      header: 'Prefix',
      render: k => <code className="text-xs">{k.prefix}…</code>,
    },
    {
      key: 'scopes',
      header: 'Scopes',
      render: k => (
        <span className="inline-flex gap-1 flex-wrap">
          {k.scopes.map(s => <Pill key={s} tone={scopeTone(s)}>{s}</Pill>)}
        </span>
      ),
    },
    {
      key: 'created',
      header: 'Created',
      render: k => <span className="text-text-muted">{formatDateTime(k.createdAt)}</span>,
    },
    {
      key: 'lastUsed',
      header: 'Last used',
      render: k => k.lastUsedAt
        ? <span className="text-text-muted">{formatDateTime(k.lastUsedAt)}</span>
        : <span className="text-text-muted">Never</span>,
    },
    {
      key: 'expires',
      header: 'Expires',
      render: k => {
        if (!k.expiresAt) return <span className="text-text-muted">Never</span>;
        const date = new Date(k.expiresAt);
        const expired = !Number.isNaN(date.getTime()) && date < new Date();
        return expired
          ? <Pill tone="crit">Expired</Pill>
          : <Pill>{formatDateTime(k.expiresAt)}</Pill>;
      },
    },
    {
      key: 'status',
      header: 'Status',
      render: k => k.isActive
        ? <Pill tone="ok">Active</Pill>
        : <Pill tone="crit">Revoked</Pill>,
    },
    {
      key: 'actions',
      header: '',
      render: k => k.isActive
        ? (
          <Button
            type="button"
            variant="danger"
            size="sm"
            onClick={() => setRevoking(k)}
          >
            Revoke
          </Button>
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
    } catch {
      // createSimpleMutation already surfaced the error toast — keep the
      // confirm dialog open so the user can retry.
    }
  };

  return (
    <div className="flex flex-col gap-3 p-7">
      <PageHeader
        eyebrow="Access"
        prefix="API"
        emphasis="keys"
        sub={
          isLoading
            ? <span className="text-text-muted">Loading…</span>
            : <span className="text-text-muted">{formatNumber(entries.length)} total</span>
        }
        actions={
          <>
            <Button icon={<RefreshCw />} type="button" onClick={() => refetch()} disabled={isLoading}>
              Refresh
            </Button>
            <Button variant="primary" icon={<Key />} type="button" onClick={() => setGenerateOpen(true)}>
              Generate key
            </Button>
          </>
        }
      />

      <Banner
        tone="warn"
        icon={<Shield />}
        title="Treat API keys like passwords."
        sub="Rotate them regularly. Revoke any key that may have been exposed."
      />

      {isError && (
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load API keys"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      )}

      {!isError && (
        <Card>
          <DataTable
            columns={columns}
            rows={entries}
            rowKey={k => k.id}
            gridTemplate={GRID_TEMPLATE}
            empty={
              <EmptyState
                icon={<Key />}
                title={isLoading ? 'Loading API keys…' : 'No API keys yet'}
                description={isLoading ? '' : 'Generate a key to enable programmatic access to Beacon.'}
                action={
                  isLoading ? null : (
                    <Button variant="primary" type="button" onClick={() => setGenerateOpen(true)}>
                      Generate first key
                    </Button>
                  )
                }
              />
            }
          />
        </Card>
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
