import { useState } from 'react';
import { useNavigate, useParams, Link } from 'react-router-dom';
import { toast } from 'sonner';
import { PageHeader } from '@/components/layout/PageHeader';
import { EmptyState } from '@/components/data/EmptyState';
import { Tabs } from '@/components/Tabs';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { formatDateTime } from '@/lib/format';
import {
  DataContractRuleType,
  DataContractSeverity,
  describeContractError,
  useDataContract,
  useDeleteContract,
  useEvaluateContract,
  useEvaluationHistory,
} from './queries';
import { CreateDataContractDialog } from './CreateDataContractDialog';
import { scorePillClass } from '../data-catalog/DataCatalogPage';

const RULE_TYPE_LABEL: Record<number, string> = {
  [DataContractRuleType.Volume]: 'Volume',
  [DataContractRuleType.Freshness]: 'Freshness',
  [DataContractRuleType.NullRate]: 'Null rate',
  [DataContractRuleType.Uniqueness]: 'Uniqueness',
  [DataContractRuleType.Referential]: 'Referential',
  [DataContractRuleType.Range]: 'Range',
  [DataContractRuleType.Pattern]: 'Pattern',
  [DataContractRuleType.CustomSql]: 'Custom SQL',
};

const SEVERITY_LABEL: Record<number, string> = {
  [DataContractSeverity.Low]: 'Low',
  [DataContractSeverity.Medium]: 'Medium',
  [DataContractSeverity.High]: 'High',
  [DataContractSeverity.Critical]: 'Critical',
};

type TabKey = 'rules' | 'evaluations' | 'latest';

export default function DataContractDetailPage() {
  const params = useParams();
  const navigate = useNavigate();
  const id = Number(params.id);
  const contractQ = useDataContract(Number.isFinite(id) ? id : null);
  const historyQ = useEvaluationHistory(Number.isFinite(id) ? id : null);
  const evaluateMutation = useEvaluateContract(id);
  const deleteMutation = useDeleteContract();

  const [tab, setTab] = useState<TabKey>('rules');
  const [editing, setEditing] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);

  if (!Number.isFinite(id)) {
    return (
      <div className="page">
        <EmptyState title="Invalid contract id" />
      </div>
    );
  }

  if (contractQ.isLoading) {
    return (
      <div className="page">
        <PageHeader title="Contract" sub={<span className="muted">Loading…</span>} />
      </div>
    );
  }

  if (contractQ.isError || !contractQ.data) {
    return (
      <div className="page">
        <EmptyState
          title="Contract not found"
          description={
            contractQ.error instanceof Error ? contractQ.error.message : 'It may have been deleted.'
          }
        />
      </div>
    );
  }

  const c = contractQ.data;
  const evaluations = historyQ.data?.evaluations ?? [];
  const latest = evaluations[0];

  function handleEvaluate() {
    evaluateMutation.mutate(undefined, {
      onSuccess: r =>
        toast.success(
          `Evaluation complete: ${r.overallScore.toFixed(1)}% (${r.passedRules}/${r.totalRules} passed)`,
        ),
      onError: err => toast.error(describeContractError(err, 'Evaluation failed')),
    });
  }

  function handleDelete() {
    deleteMutation.mutate(id, {
      onSuccess: () => {
        toast.success('Contract deleted.');
        navigate('/data-quality');
      },
      onError: err => toast.error(describeContractError(err, 'Delete failed')),
    });
  }

  return (
    <div className="page">
      <PageHeader
        title={c.name}
        sub={`${c.dataSourceName} / ${c.schemaName}.${c.tableName}`}
        actions={
          <>
            <button
              type="button"
              className="btn btn--primary"
              onClick={handleEvaluate}
              disabled={evaluateMutation.isPending}
            >
              {evaluateMutation.isPending ? 'Evaluating…' : 'Evaluate now'}
            </button>
            <button type="button" className="btn" onClick={() => setEditing(true)}>
              Edit
            </button>
            <button type="button" className="btn btn--danger" onClick={() => setConfirmDelete(true)}>
              Delete
            </button>
          </>
        }
      />

      <p>
        <Link to="/data-quality" className="muted">← Back to data quality</Link>
      </p>

      <div
        style={{
          display: 'grid',
          gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))',
          gap: 12,
          marginBottom: 16,
        }}
      >
        <Metric label="Latest score" value={c.latestScore === null ? 'N/A' : `${c.latestScore.toFixed(0)}%`} />
        <Metric label="Rules" value={c.rules.length} />
        <Metric label="Schedule" value={c.cronExpression} mono />
        <Metric label="Failure threshold" value={`${c.failureThresholdScore}%`} />
      </div>

      <Tabs<TabKey>
        active={tab}
        onChange={setTab}
        tabs={[
          { key: 'rules', label: 'Rules', count: c.rules.length },
          { key: 'evaluations', label: 'Evaluations', count: evaluations.length },
          ...(latest ? [{ key: 'latest' as const, label: 'Latest results' }] : []),
        ]}
      />

      <div className="card" style={{ padding: 16, marginTop: 12 }}>
        {tab === 'rules' && (
          <table className="table">
            <thead>
              <tr>
                <th>Name</th>
                <th>Type</th>
                <th>Column</th>
                <th>Severity</th>
                <th>Weight</th>
                <th>Enabled</th>
              </tr>
            </thead>
            <tbody>
              {c.rules.map(r => (
                <tr key={r.id ?? r.name}>
                  <td>{r.name}</td>
                  <td>{RULE_TYPE_LABEL[r.ruleType] ?? r.ruleType}</td>
                  <td>{r.columnName ?? '—'}</td>
                  <td>{SEVERITY_LABEL[r.severity] ?? r.severity}</td>
                  <td>{r.weight}</td>
                  <td>{r.isEnabled ? 'Yes' : 'No'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}

        {tab === 'evaluations' && (
          historyQ.isLoading ? (
            <p className="muted">Loading history…</p>
          ) : evaluations.length === 0 ? (
            <EmptyState title="No evaluations yet" description="Click 'Evaluate now' to run the first check." />
          ) : (
            <table className="table">
              <thead>
                <tr>
                  <th>Time</th>
                  <th>Score</th>
                  <th>Passed</th>
                  <th>Failed</th>
                  <th>Duration (ms)</th>
                </tr>
              </thead>
              <tbody>
                {evaluations.map(ev => (
                  <tr key={ev.id}>
                    <td>{formatDateTime(ev.createdTime)}</td>
                    <td>
                      <span className={scorePillClass(ev.overallScore)}>
                        {ev.overallScore.toFixed(1)}%
                      </span>
                    </td>
                    <td>{ev.passedRules} / {ev.totalRules}</td>
                    <td>{ev.failedRules}</td>
                    <td>{ev.executionTimeMs.toFixed(0)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )
        )}

        {tab === 'latest' && latest && (
          <table className="table">
            <thead>
              <tr>
                <th>Rule</th>
                <th>Status</th>
                <th>Score</th>
                <th>Actual</th>
                <th>Expected</th>
                <th>Message</th>
              </tr>
            </thead>
            <tbody>
              {latest.ruleResults.map(r => (
                <tr key={r.id}>
                  <td>{r.ruleName}</td>
                  <td>
                    <span className={r.passed ? 'pill pill--ok' : 'pill pill--crit'}>
                      {r.passed ? 'Pass' : 'Fail'}
                    </span>
                  </td>
                  <td>{r.score.toFixed(1)}%</td>
                  <td>{r.actualValue ?? '—'}</td>
                  <td>{r.expectedValue ?? '—'}</td>
                  <td className="muted">{r.message ?? ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      {editing && (
        <CreateDataContractDialog
          editContractId={id}
          onClose={success => {
            setEditing(false);
            if (success) toast.success('Contract updated.');
          }}
        />
      )}

      <ConfirmDialog
        open={confirmDelete}
        title="Delete contract"
        message={`Delete "${c.name}"? This cannot be undone.`}
        confirmLabel="Delete"
        destructive
        busy={deleteMutation.isPending}
        onConfirm={handleDelete}
        onCancel={() => setConfirmDelete(false)}
      />
    </div>
  );
}

function Metric({ label, value, mono }: { label: string; value: React.ReactNode; mono?: boolean }) {
  return (
    <div className="card" style={{ padding: 12 }}>
      <div className="muted" style={{ fontSize: 11 }}>{label}</div>
      <div
        style={{
          fontWeight: 700,
          fontSize: 18,
          fontFamily: mono ? 'var(--font-mono, monospace)' : undefined,
          marginTop: 4,
        }}
      >
        {value}
      </div>
    </div>
  );
}
