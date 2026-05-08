import { useState } from 'react';
import { Link } from 'react-router-dom';
import { toast } from 'sonner';
import { PageHeader } from '@/components/layout/PageHeader';
import { EmptyState } from '@/components/data/EmptyState';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import {
  describeContractError,
  useDataContracts,
  useDataQualityOverview,
  useDeleteContract,
  type DataContractData,
  type DataQualityOverviewData,
} from './queries';
import { CreateDataContractDialog } from './CreateDataContractDialog';
import { scorePillClass } from '../data-catalog/DataCatalogPage';

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
    <div className="page">
      <PageHeader
        title="Data quality"
        sub="Monitor data quality across data sources with automated contracts and checks."
        actions={
          <button type="button" className="btn btn--primary" onClick={() => setCreateOpen(true)}>
            New contract
          </button>
        }
      />

      {overviewQ.isLoading && <p className="muted">Loading overview…</p>}
      {overviews.length > 0 && (
        <div
          style={{
            display: 'grid',
            gridTemplateColumns: 'repeat(auto-fill, minmax(260px, 1fr))',
            gap: 12,
            marginBottom: 24,
          }}
        >
          {overviews.map(o => (
            <OverviewCard key={o.dataSourceId} overview={o} />
          ))}
        </div>
      )}

      <h2 style={{ marginTop: 24, marginBottom: 12 }}>Contracts</h2>

      {contractsQ.isLoading && <p className="muted">Loading contracts…</p>}
      {!contractsQ.isLoading && contracts.length === 0 && (
        <EmptyState
          title="No contracts yet"
          description="Create a contract to start monitoring data quality on a table."
        />
      )}
      {contracts.length > 0 && (
        <div className="card" style={{ padding: 0, overflow: 'hidden' }}>
          <table className="table">
            <thead>
              <tr>
                <th>Name</th>
                <th>Table</th>
                <th>Data source</th>
                <th>Score</th>
                <th>Status</th>
                <th>Schedule</th>
                <th style={{ width: 1 }}>Actions</th>
              </tr>
            </thead>
            <tbody>
              {contracts.map(c => (
                <tr key={c.id}>
                  <td>
                    <Link to={`/data-quality/${c.id}`}>{c.name}</Link>
                  </td>
                  <td className="muted">{c.schemaName}.{c.tableName}</td>
                  <td>{c.dataSourceName}</td>
                  <td>
                    {c.latestScore === null ? (
                      <span className="muted">Not evaluated</span>
                    ) : (
                      <span className={scorePillClass(c.latestScore)}>
                        {c.latestScore.toFixed(0)}%
                      </span>
                    )}
                  </td>
                  <td>
                    <span className={c.isEnabled ? 'pill pill--ok' : 'pill pill--neutral'}>
                      {c.isEnabled ? 'Active' : 'Disabled'}
                    </span>
                  </td>
                  <td><code>{c.cronExpression}</code></td>
                  <td>
                    <div style={{ display: 'flex', gap: 6 }}>
                      <Link to={`/data-quality/${c.id}`} className="btn btn--ghost btn--sm">
                        View
                      </Link>
                      <button
                        type="button"
                        className="btn btn--ghost btn--sm"
                        onClick={() => setEditId(c.id)}
                      >
                        Edit
                      </button>
                      <button
                        type="button"
                        className="btn btn--ghost btn--sm"
                        onClick={() => setDeleteTarget(c)}
                      >
                        Delete
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
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
    <div className="card" style={{ padding: 14 }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 8 }}>
        <strong>{o.dataSourceName}</strong>
        <span className={scorePillClass(o.averageScore)}>{o.averageScore.toFixed(0)}%</span>
      </div>
      <div style={{ display: 'flex', gap: 16, marginTop: 10 }}>
        <Stat label="Healthy" value={o.healthyTables} tone="ok" />
        <Stat label="Degrading" value={o.degradingTables} tone="crit" />
        <Stat label="Contracts" value={o.activeContracts} />
      </div>
      {o.tableScores.length > 0 && (
        <>
          <hr style={{ margin: '10px 0', border: 0, borderTop: '1px solid var(--border)' }} />
          <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
            {o.tableScores.slice(0, 5).map((s, i) => (
              <div key={i} style={{ display: 'flex', justifyContent: 'space-between', fontSize: 13 }}>
                <span className="muted">{s.schemaName}.{s.tableName}</span>
                <span className={scorePillClass(s.score)}>{s.score.toFixed(0)}%</span>
              </div>
            ))}
          </div>
        </>
      )}
    </div>
  );
}

function Stat({ label, value, tone }: { label: string; value: number; tone?: 'ok' | 'crit' }) {
  const color =
    tone === 'ok' ? 'var(--ok)' : tone === 'crit' ? 'var(--crit)' : 'var(--text)';
  return (
    <div>
      <div style={{ fontWeight: 700, fontSize: 18, color }}>{value}</div>
      <div className="muted" style={{ fontSize: 11 }}>{label}</div>
    </div>
  );
}
