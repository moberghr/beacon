import { useState } from 'react';
import { useNavigate, useParams, Link } from 'react-router-dom';
import { toast } from 'sonner';
import { PageHeader, Button, Card, KPI, KPIGrid, Pill } from '@/components/beacon';
import { EmptyState } from '@/components/data/EmptyState';
import { Tabs } from '@/components/Tabs';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { formatDateTime } from '@/lib/format';
import {
  DataContractRuleType,
  DataContractSeverity,
  describeContractError,
  scoreTone,
  useDataContract,
  useDeleteContract,
  useEvaluateContract,
  useEvaluationHistory,
} from './queries';
import { CreateDataContractDialog } from './CreateDataContractDialog';

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
      <div className="flex flex-col gap-5 p-7">
        <EmptyState title="Invalid contract id" />
      </div>
    );
  }

  if (contractQ.isLoading) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <PageHeader variant="nodes" emphasis="Contract" sub={<span className="text-text-muted">Loading…</span>} />
      </div>
    );
  }

  if (contractQ.isError || !contractQ.data) {
    return (
      <div className="flex flex-col gap-5 p-7">
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
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        variant="nodes"
        eyebrow={<>Data quality <span className="eyebrow-sep">/</span> <span className="mono">#{c.id}</span></>}
        emphasis={c.name}
        sub={`${c.dataSourceName} / ${c.schemaName}.${c.tableName}`}
        actions={
          <>
            <Button
              variant="primary"
              type="button"
              onClick={handleEvaluate}
              disabled={evaluateMutation.isPending}
            >
              {evaluateMutation.isPending ? 'Evaluating…' : 'Evaluate now'}
            </Button>
            <Button type="button" onClick={() => setEditing(true)}>Edit</Button>
            <Button variant="danger" type="button" onClick={() => setConfirmDelete(true)}>Delete</Button>
          </>
        }
      />

      <div>
        <Link to="/data-quality" className="text-text-muted text-sm">← Back to data quality</Link>
      </div>

      <KPIGrid>
        <KPI dot="brand" label="Latest score" value={c.latestScore === null ? 'N/A' : `${c.latestScore.toFixed(0)}%`} />
        <KPI dot="info" label="Rules" value={c.rules.length} />
        <KPI dot="ok" label="Schedule" value={<span className="mono text-base">{c.cronExpression}</span>} />
        <KPI dot="warn" label="Failure threshold" value={`${c.failureThresholdScore}%`} />
      </KPIGrid>

      <Tabs<TabKey>
        active={tab}
        onChange={setTab}
        tabs={[
          { key: 'rules', label: 'Rules', count: c.rules.length },
          { key: 'evaluations', label: 'Evaluations', count: evaluations.length },
          ...(latest ? [{ key: 'latest' as const, label: 'Latest results' }] : []),
        ]}
      />

      <Card className="p-4">
        {tab === 'rules' && (
          <table className="w-full text-sm">
            <thead>
              <tr className="text-left text-2xs font-semibold uppercase tracking-eyebrow text-text-muted border-b border-border">
                <th className="px-2 py-2">Name</th>
                <th className="px-2 py-2">Type</th>
                <th className="px-2 py-2">Column</th>
                <th className="px-2 py-2">Severity</th>
                <th className="px-2 py-2">Weight</th>
                <th className="px-2 py-2">Enabled</th>
              </tr>
            </thead>
            <tbody>
              {c.rules.map(r => (
                <tr key={r.id ?? r.name} className="border-b border-border last:border-b-0">
                  <td className="px-2 py-2">{r.name}</td>
                  <td className="px-2 py-2">{RULE_TYPE_LABEL[r.ruleType] ?? r.ruleType}</td>
                  <td className="px-2 py-2">{r.columnName ?? '—'}</td>
                  <td className="px-2 py-2">{SEVERITY_LABEL[r.severity] ?? r.severity}</td>
                  <td className="px-2 py-2">{r.weight}</td>
                  <td className="px-2 py-2">{r.isEnabled ? 'Yes' : 'No'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}

        {tab === 'evaluations' && (
          historyQ.isLoading ? (
            <p className="text-text-muted">Loading history…</p>
          ) : evaluations.length === 0 ? (
            <EmptyState title="No evaluations yet" description="Click 'Evaluate now' to run the first check." />
          ) : (
            <table className="w-full text-sm">
              <thead>
                <tr className="text-left text-2xs font-semibold uppercase tracking-eyebrow text-text-muted border-b border-border">
                  <th className="px-2 py-2">Time</th>
                  <th className="px-2 py-2">Score</th>
                  <th className="px-2 py-2">Passed</th>
                  <th className="px-2 py-2">Failed</th>
                  <th className="px-2 py-2">Duration (ms)</th>
                </tr>
              </thead>
              <tbody>
                {evaluations.map(ev => (
                  <tr key={ev.id} className="border-b border-border last:border-b-0">
                    <td className="px-2 py-2">{formatDateTime(ev.createdTime)}</td>
                    <td className="px-2 py-2">
                      <Pill tone={scoreTone(ev.overallScore)}>{ev.overallScore.toFixed(1)}%</Pill>
                    </td>
                    <td className="px-2 py-2">{ev.passedRules} / {ev.totalRules}</td>
                    <td className="px-2 py-2">{ev.failedRules}</td>
                    <td className="px-2 py-2">{ev.executionTimeMs.toFixed(0)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )
        )}

        {tab === 'latest' && latest && (
          <table className="w-full text-sm">
            <thead>
              <tr className="text-left text-2xs font-semibold uppercase tracking-eyebrow text-text-muted border-b border-border">
                <th className="px-2 py-2">Rule</th>
                <th className="px-2 py-2">Status</th>
                <th className="px-2 py-2">Score</th>
                <th className="px-2 py-2">Actual</th>
                <th className="px-2 py-2">Expected</th>
                <th className="px-2 py-2">Message</th>
              </tr>
            </thead>
            <tbody>
              {latest.ruleResults.map(r => (
                <tr key={r.id} className="border-b border-border last:border-b-0">
                  <td className="px-2 py-2">{r.ruleName}</td>
                  <td className="px-2 py-2">
                    <Pill tone={r.passed ? 'ok' : 'crit'}>{r.passed ? 'Pass' : 'Fail'}</Pill>
                  </td>
                  <td className="px-2 py-2">{r.score.toFixed(1)}%</td>
                  <td className="px-2 py-2">{r.actualValue ?? '—'}</td>
                  <td className="px-2 py-2">{r.expectedValue ?? '—'}</td>
                  <td className="px-2 py-2 text-text-muted">{r.message ?? ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>

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
