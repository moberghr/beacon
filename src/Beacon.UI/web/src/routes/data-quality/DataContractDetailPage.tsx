import { useState } from 'react';
import { useNavigate, useParams, Link } from 'react-router-dom';
import { useAccess } from '@/auth/useAuth';
import { toast } from 'sonner';
import { UserX } from 'lucide-react';
import { PageHeader, Banner, Button, Card, KPI, KPIGrid, Pill } from '@/components/beacon';
import { EmptyState } from '@/components/data/EmptyState';
import { isNotFound, NOT_FOUND_DETAIL } from '@/lib/api';
import { Tabs } from '@/components/Tabs';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { ChangeApprovalKind, DataContractRuleType, DataContractSeverity } from '@/lib/enums';
import { formatDateTime } from '@/lib/format';
import {
  evaluationNote,
  scoreTone,
  useDataContract,
  useDeleteContract,
  useEvaluateContract,
  useEvaluationHistory,
  useSetContractOwner,
} from './queries';
import { CreateDataContractDialog } from './CreateDataContractDialog';
import { SetOwnerDialog } from '../users/SetOwnerDialog';
import { AwaitingApprovalBanner, AwaitingApprovalPill } from '../approvals/AwaitingApproval';

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

/** The owner by name, never by id: a neutral label when none is recorded or the user no longer exists. */
function ownerLabel(contract: { hasOwner: boolean; ownerDisplayName: string | null }): string {
  if (!contract.hasOwner) {
    return 'No owner recorded';
  }
  return contract.ownerDisplayName ?? 'Unknown user';
}

export default function DataContractDetailPage() {
  const params = useParams();
  const navigate = useNavigate();
  const id = Number(params.id);
  const contractQ = useDataContract(Number.isFinite(id) ? id : null);
  const historyQ = useEvaluationHistory(Number.isFinite(id) ? id : null);
  const evaluateMutation = useEvaluateContract(id);
  const deleteMutation = useDeleteContract();
  const setOwnerMutation = useSetContractOwner(id);
  // Giving a contract a new owner is a UserAdmin's (an Admin holds it).
  const canChangeOwner = useAccess().hasPermission('UserAdmin');
  const [changingOwner, setChangingOwner] = useState(false);

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
          title={contractQ.isError && !isNotFound(contractQ.error) ? 'Failed to load contract' : 'Contract not found'}
          description={
            !contractQ.isError || isNotFound(contractQ.error)
              ? NOT_FOUND_DETAIL
              : contractQ.error instanceof Error ? contractQ.error.message : 'Unknown error'
          }
        />
      </div>
    );
  }

  const c = contractQ.data;
  const evaluations = historyQ.data?.evaluations ?? [];
  const latest = evaluations[0];

  function handleEvaluate() {
    // No onError toast — useEvaluateContract (createSimpleMutation) already toasts.
    evaluateMutation.mutate(undefined, {
      onSuccess: r => {
        const note = evaluationNote(r);
        toast.success(
          `Evaluation complete: ${r.overallScore.toFixed(1)}% (${r.passedRules}/${r.totalRules} passed)`,
          note ? { description: note } : undefined,
        );
      },
    });
  }

  function handleDelete() {
    // No onError toast — useDeleteContract (createSimpleMutation) already toasts.
    deleteMutation.mutate(id, {
      onSuccess: () => {
        toast.success('Contract deleted.');
        navigate('/data-quality');
      },
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
            {canChangeOwner && (
              <Button type="button" onClick={() => setChangingOwner(true)}>Change owner</Button>
            )}
            <Button variant="danger" type="button" onClick={() => setConfirmDelete(true)}>Delete</Button>
          </>
        }
      />

      {canChangeOwner && changingOwner && (
        <SetOwnerDialog
          open
          noun="data contract"
          description="Scheduled evaluations run as the owner, so the owner must be able to run this contract's rules on its data source. Admins can always change the contract."
          busy={setOwnerMutation.isPending}
          onClose={() => setChangingOwner(false)}
          onSubmit={userId => setOwnerMutation.mutate(userId, { onSuccess: () => setChangingOwner(false) })}
        />
      )}

      <div>
        <Link to="/data-quality" className="text-text-muted text-sm">← Back to data quality</Link>
      </div>

      {c.ownerNotAuthorized && (
        <Banner
          tone="warn"
          icon={<UserX />}
          title="Owner not authorized: scheduled evaluations are skipped"
          sub={
            canChangeOwner
              ? 'The owner this contract runs as may no longer run its rules, so each scheduled evaluation is skipped. Assign an owner who has access to resume it.'
              : 'The owner this contract runs as may no longer run its rules, so each scheduled evaluation is skipped until an Admin or a UserAdmin assigns an owner who has access.'
          }
          actions={
            canChangeOwner ? (
              <Button size="sm" onClick={() => setChangingOwner(true)}>
                Change owner
              </Button>
            ) : undefined
          }
        />
      )}

      {c.rules.some(r => r.awaitingApproval) && (
        <AwaitingApprovalBanner
          kind={ChangeApprovalKind.CustomSqlRules}
          title="Custom SQL awaiting approval: those rules are not evaluated"
          sub="A second person approves a new or changed Custom SQL rule before it runs; the contract's other rules keep running, and their score is marked partial."
          requestId={c.pendingApprovalRequestId}
          lastDecision={c.lastApprovalDecision}
        />
      )}

      <KPIGrid>
        <KPI
          dot="brand"
          label="Latest score"
          value={c.latestScore === null ? 'N/A' : `${c.latestScore.toFixed(0)}%`}
          // A partial evaluation does not replace the table's score: this stays the last complete one.
          sub={latest?.isPartial ? 'the latest evaluation was partial and did not update it' : undefined}
        />
        <KPI dot="info" label="Rules" value={c.rules.length} />
        <KPI dot="ok" label="Schedule" value={<span className="mono text-base">{c.cronExpression}</span>} />
        <KPI dot="warn" label="Failure threshold" value={`${c.failureThresholdScore}%`} />
        <KPI
          dot="info"
          label="Owner"
          value={<span className="text-base">{ownerLabel(c)}</span>}
          sub={c.hasOwner ? 'scheduled evaluations run as the owner' : 'created before owners were recorded'}
        />
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
                  <td className="px-2 py-2">
                    <span className="flex items-center gap-2">
                      {r.name}
                      {r.awaitingApproval && (
                        <AwaitingApprovalPill
                          kind={ChangeApprovalKind.CustomSqlRules}
                          requestId={c.pendingApprovalRequestId}
                          lastDecision={c.lastApprovalDecision}
                        />
                      )}
                    </span>
                  </td>
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
                  <th className="px-2 py-2">Notes</th>
                </tr>
              </thead>
              <tbody>
                {evaluations.map(ev => (
                  <tr key={ev.id} className="border-b border-border last:border-b-0">
                    <td className="px-2 py-2">{formatDateTime(ev.createdTime)}</td>
                    <td className="px-2 py-2">
                      <span className="flex items-center gap-1.5">
                        <Pill tone={scoreTone(ev.overallScore)}>{ev.overallScore.toFixed(1)}%</Pill>
                        {ev.isPartial && (
                          <Pill tone="warn" title="Rules awaiting approval were left out: the score does not stand for the whole contract">
                            Partial
                          </Pill>
                        )}
                      </span>
                    </td>
                    <td className="px-2 py-2">{ev.passedRules} / {ev.totalRules}</td>
                    <td className="px-2 py-2">{ev.failedRules}</td>
                    <td className="px-2 py-2">{ev.executionTimeMs.toFixed(0)}</td>
                    <td className="px-2 py-2 text-text-muted text-xs">{evaluationNote(ev) ?? '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )
        )}

        {tab === 'latest' && latest && evaluationNote(latest) && (
          <Banner
            tone="warn"
            className="mb-3"
            title={latest.isPartial ? 'Partial evaluation: the score leaves out rules awaiting approval' : 'The alert was held back for some recipients'}
            sub={evaluationNote(latest)}
          />
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
