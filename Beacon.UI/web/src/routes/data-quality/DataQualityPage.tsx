import { useState } from 'react';
import { Link } from 'react-router-dom';
import { toast } from 'sonner';
import { Plus } from 'lucide-react';
import { PageHeader, Button, Card, Pill } from '@/components/beacon';
import { EmptyState } from '@/components/data/EmptyState';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import {
  describeContractError,
  scoreTone,
  useDataContracts,
  useDataQualityOverview,
  useDeleteContract,
  type DataContractData,
  type DataQualityOverviewData,
} from './queries';
import { CreateDataContractDialog } from './CreateDataContractDialog';

export default function DataQualityPage() {
  const overviewQ = useDataQualityOverview();
  const contractsQ = useDataContracts();
  const deleteMutation = useDeleteContract();

  const [createOpen, setCreateOpen] = useState(false);
  const [editId, setEditId] = useState<number | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<DataContractData | null>(null);

  const overviews = overviewQ.data ?? [];
  const contracts = contractsQ.data ?? [];

  function handleConfirmDelete() {
    if (!deleteTarget) return;
    const name = deleteTarget.name;
    const id = deleteTarget.id;
    deleteMutation.mutate(id, {
      onSuccess: () => {
        toast.success(`Deleted contract "${name}".`);
        setDeleteTarget(null);
      },
      onError: err => toast.error(describeContractError(err, 'Delete failed')),
    });
  }

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        variant="nodes"
        eyebrow="Data"
        prefix="Monitoring"
        emphasis="data quality"
        sub="Monitor data quality across data sources with automated contracts and checks."
        actions={
          <Button variant="primary" type="button" onClick={() => setCreateOpen(true)} icon={<Plus />}>
            New contract
          </Button>
        }
      />

      {overviewQ.isLoading && <p className="text-text-muted">Loading overview…</p>}
      {overviews.length > 0 && (
        <div className="grid gap-3 grid-cols-[repeat(auto-fill,minmax(260px,1fr))]">
          {overviews.map(o => (
            <OverviewCard key={o.dataSourceId} overview={o} />
          ))}
        </div>
      )}

      <h2 className="text-lg font-semibold mt-2 mb-0">Contracts</h2>

      {contractsQ.isLoading && <p className="text-text-muted">Loading contracts…</p>}
      {!contractsQ.isLoading && contracts.length === 0 && (
        <EmptyState
          title="No contracts yet"
          description="Create a contract to start monitoring data quality on a table."
        />
      )}
      {contracts.length > 0 && (
        <Card className="overflow-hidden">
          <table className="w-full text-sm">
            <thead>
              <tr className="text-left text-2xs font-semibold uppercase tracking-eyebrow text-text-muted bg-surface-2 border-b border-border">
                <th className="px-3 py-2">Name</th>
                <th className="px-3 py-2">Table</th>
                <th className="px-3 py-2">Data source</th>
                <th className="px-3 py-2">Score</th>
                <th className="px-3 py-2">Status</th>
                <th className="px-3 py-2">Schedule</th>
                <th className="px-3 py-2 w-px">Actions</th>
              </tr>
            </thead>
            <tbody>
              {contracts.map(c => (
                <tr key={c.id} className="border-b border-border last:border-b-0">
                  <td className="px-3 py-2">
                    <Link to={`/data-quality/${c.id}`} className="text-brand-600 hover:underline">{c.name}</Link>
                  </td>
                  <td className="px-3 py-2 text-text-muted">{c.schemaName}.{c.tableName}</td>
                  <td className="px-3 py-2">{c.dataSourceName}</td>
                  <td className="px-3 py-2">
                    {c.latestScore === null ? (
                      <span className="text-text-muted">Not evaluated</span>
                    ) : (
                      <Pill tone={scoreTone(c.latestScore)}>{c.latestScore.toFixed(0)}%</Pill>
                    )}
                  </td>
                  <td className="px-3 py-2">
                    <Pill tone={c.isEnabled ? 'ok' : 'neutral'}>
                      {c.isEnabled ? 'Active' : 'Disabled'}
                    </Pill>
                  </td>
                  <td className="px-3 py-2"><code className="mono text-xs">{c.cronExpression}</code></td>
                  <td className="px-3 py-2">
                    <div className="flex gap-1.5">
                      <Link to={`/data-quality/${c.id}`}>
                        <Button variant="ghost" size="sm">View</Button>
                      </Link>
                      <Button variant="ghost" size="sm" type="button" onClick={() => setEditId(c.id)}>Edit</Button>
                      <Button variant="ghost" size="sm" type="button" onClick={() => setDeleteTarget(c)}>Delete</Button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </Card>
      )}

      {createOpen && (
        <CreateDataContractDialog
          editContractId={null}
          onClose={success => {
            setCreateOpen(false);
            if (success) toast.success('Contract created.');
          }}
        />
      )}
      {editId !== null && (
        <CreateDataContractDialog
          editContractId={editId}
          onClose={success => {
            setEditId(null);
            if (success) toast.success('Contract updated.');
          }}
        />
      )}
      <ConfirmDialog
        open={deleteTarget !== null}
        title="Delete contract"
        message={
          deleteTarget
            ? `Delete "${deleteTarget.name}"? This cannot be undone.`
            : ''
        }
        confirmLabel="Delete"
        destructive
        busy={deleteMutation.isPending}
        onConfirm={handleConfirmDelete}
        onCancel={() => setDeleteTarget(null)}
      />
    </div>
  );
}

function OverviewCard({ overview: o }: { overview: DataQualityOverviewData }) {
  return (
    <Card className="p-3.5">
      <div className="flex items-center justify-between gap-2">
        <strong>{o.dataSourceName}</strong>
        <Pill tone={scoreTone(o.averageScore)}>{o.averageScore.toFixed(0)}%</Pill>
      </div>
      <div className="flex gap-4 mt-2.5">
        <Stat label="Healthy" value={o.healthyTables} tone="ok" />
        <Stat label="Degrading" value={o.degradingTables} tone="crit" />
        <Stat label="Contracts" value={o.activeContracts} />
      </div>
      {o.tableScores.length > 0 && (
        <>
          <hr className="my-2.5 border-0 border-t border-border" />
          <div className="flex flex-col gap-1">
            {o.tableScores.slice(0, 5).map((s, i) => (
              <div key={i} className="flex justify-between text-[13px]">
                <span className="text-text-muted">{s.schemaName}.{s.tableName}</span>
                <Pill tone={scoreTone(s.score)}>{s.score.toFixed(0)}%</Pill>
              </div>
            ))}
          </div>
        </>
      )}
    </Card>
  );
}

function Stat({ label, value, tone }: { label: string; value: number; tone?: 'ok' | 'crit' }) {
  const color =
    tone === 'ok' ? 'text-ok' : tone === 'crit' ? 'text-crit' : 'text-text';
  return (
    <div>
      <div className={`font-bold text-lg ${color}`}>{value}</div>
      <div className="text-text-muted text-[11px]">{label}</div>
    </div>
  );
}
