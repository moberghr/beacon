import { useMemo, useState } from 'react';
import { toast } from 'sonner';
import { PageHeader } from '@/components/layout/PageHeader';
import { EmptyState } from '@/components/data/EmptyState';
import { Tabs } from '@/components/Tabs';
import {
  describeMcpError,
  McpDocPatchStatus,
  McpPatternStatus,
  PATCH_STATUS_LABEL,
  PATTERN_STATUS_LABEL,
  PATTERN_TYPE_LABEL,
  useApplyPatch,
  useDocumentationPatches,
  useLearnedPatterns,
  useLearningStats,
  useRejectPatch,
  useUpdatePatternStatus,
} from './queries';

type TabKey = 'patterns' | 'patches' | 'problems';

export default function McpLearningPage() {
  const statsQ = useLearningStats();
  const patternsQ = useLearnedPatterns();
  const patchesQ = useDocumentationPatches();
  const updatePattern = useUpdatePatternStatus();
  const applyPatch = useApplyPatch();
  const rejectPatch = useRejectPatch();

  const [tab, setTab] = useState<TabKey>('patterns');
  const [statusFilter, setStatusFilter] = useState<string>('');
  const [typeFilter, setTypeFilter] = useState<string>('');

  const stats = statsQ.data;
  const patterns = patternsQ.data?.patterns ?? [];
  const patches = patchesQ.data?.patches ?? [];

  const filteredPatterns = useMemo(
    () =>
      patterns.filter(p => {
        if (statusFilter !== '' && p.status !== Number(statusFilter)) return false;
        if (typeFilter !== '' && p.patternType !== Number(typeFilter)) return false;
        return true;
      }),
    [patterns, statusFilter, typeFilter],
  );

  function handleUpdatePattern(id: number, status: number) {
    updatePattern.mutate(
      { patternId: id, newStatus: status },
      {
        onSuccess: () => toast.success(status === McpPatternStatus.Approved ? 'Pattern approved.' : 'Pattern rejected.'),
        onError: err => toast.error(describeMcpError(err, 'Update failed')),
      },
    );
  }

  function handleApply(id: number) {
    applyPatch.mutate(id, {
      onSuccess: () => toast.success('Patch applied.'),
      onError: err => toast.error(describeMcpError(err, 'Apply failed')),
    });
  }

  function handleReject(id: number) {
    rejectPatch.mutate(id, {
      onSuccess: () => toast.success('Patch rejected.'),
      onError: err => toast.error(describeMcpError(err, 'Reject failed')),
    });
  }

  return (
    <div className="page">
      <PageHeader
        title="MCP learning"
        sub="The MCP server learns from every query. Review patterns, manage documentation improvements."
      />

      {statsQ.isLoading && <p className="muted">Loading stats…</p>}
      {stats && (
        <div
          style={{
            display: 'grid',
            gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))',
            gap: 12,
            marginBottom: 16,
          }}
        >
          <StatCard label="Signals (7d / 30d)" value={`${stats.signals7d} / ${stats.signals30d}`} />
          <StatCard
            label="Success rate"
            value={`${(stats.successRate * 100).toFixed(0)}%`}
            tone={stats.successRate >= 0.8 ? 'ok' : stats.successRate >= 0.5 ? 'warn' : 'crit'}
          />
          <StatCard
            label="Patterns approved"
            value={stats.patternsApproved}
            sub={stats.patternsPending > 0 ? `${stats.patternsPending} pending` : undefined}
          />
          <StatCard
            label="Doc patches applied"
            value={stats.patchesApplied}
            sub={stats.patchesProposed > 0 ? `${stats.patchesProposed} proposed` : undefined}
          />
        </div>
      )}

      <Tabs<TabKey>
        active={tab}
        onChange={setTab}
        tabs={[
          {
            key: 'patterns',
            label: 'Learned patterns',
            count: stats?.patternsPending,
          },
          { key: 'patches', label: 'Doc patches', count: stats?.patchesProposed },
          { key: 'problems', label: 'Problem tables' },
        ]}
      />

      <div className="card" style={{ padding: 16, marginTop: 12 }}>
        {tab === 'patterns' && (
          <>
            <div style={{ display: 'flex', gap: 8, marginBottom: 12 }}>
              <select className="select" value={statusFilter} onChange={e => setStatusFilter(e.target.value)}>
                <option value="">All statuses</option>
                {Object.entries(PATTERN_STATUS_LABEL).map(([k, label]) => (
                  <option key={k} value={k}>{label}</option>
                ))}
              </select>
              <select className="select" value={typeFilter} onChange={e => setTypeFilter(e.target.value)}>
                <option value="">All types</option>
                {Object.entries(PATTERN_TYPE_LABEL).map(([k, label]) => (
                  <option key={k} value={k}>{label}</option>
                ))}
              </select>
            </div>
            {patternsQ.isLoading ? (
              <p className="muted">Loading patterns…</p>
            ) : filteredPatterns.length === 0 ? (
              <EmptyState title="No patterns" description="No learned patterns match the current filters." />
            ) : (
              <table className="table">
                <thead>
                  <tr>
                    <th>Type</th>
                    <th>Table</th>
                    <th>Column</th>
                    <th>Content</th>
                    <th>Conf.</th>
                    <th>Signals</th>
                    <th>Status</th>
                    <th>Actions</th>
                  </tr>
                </thead>
                <tbody>
                  {filteredPatterns.map(p => (
                    <tr key={p.id}>
                      <td>{PATTERN_TYPE_LABEL[p.patternType] ?? p.patternType}</td>
                      <td>{p.tableName}</td>
                      <td>{p.columnName ?? '—'}</td>
                      <td
                        title={p.patternContent}
                        style={{
                          maxWidth: 300,
                          overflow: 'hidden',
                          textOverflow: 'ellipsis',
                          whiteSpace: 'nowrap',
                        }}
                      >
                        {p.patternContent}
                      </td>
                      <td>{(p.confidence * 100).toFixed(0)}%</td>
                      <td>{p.signalCount}</td>
                      <td>
                        <span className={statusPillClass(p.status)}>
                          {PATTERN_STATUS_LABEL[p.status] ?? p.status}
                        </span>
                      </td>
                      <td>
                        {p.status === McpPatternStatus.Pending && (
                          <div style={{ display: 'flex', gap: 4 }}>
                            <button
                              type="button"
                              className="btn btn--sm btn--primary"
                              onClick={() => handleUpdatePattern(p.id, McpPatternStatus.Approved)}
                            >
                              Approve
                            </button>
                            <button
                              type="button"
                              className="btn btn--sm btn--danger"
                              onClick={() => handleUpdatePattern(p.id, McpPatternStatus.Rejected)}
                            >
                              Reject
                            </button>
                          </div>
                        )}
                        {p.status === McpPatternStatus.AutoApproved && (
                          <button
                            type="button"
                            className="btn btn--sm btn--danger"
                            onClick={() => handleUpdatePattern(p.id, McpPatternStatus.Rejected)}
                          >
                            Reject
                          </button>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </>
        )}

        {tab === 'patches' && (
          patchesQ.isLoading ? (
            <p className="muted">Loading patches…</p>
          ) : patches.length === 0 ? (
            <EmptyState title="No documentation patches" />
          ) : (
            <table className="table">
              <thead>
                <tr>
                  <th>Target</th>
                  <th>Identifier</th>
                  <th>Current</th>
                  <th>Proposed</th>
                  <th>Signals</th>
                  <th>Status</th>
                  <th>Actions</th>
                </tr>
              </thead>
              <tbody>
                {patches.map(p => (
                  <tr key={p.id}>
                    <td>{p.targetType}</td>
                    <td>{p.targetIdentifier}</td>
                    <td className="muted" style={{ maxWidth: 220, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                      {p.currentContent ?? '—'}
                    </td>
                    <td style={{ maxWidth: 220, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                      {p.proposedContent}
                    </td>
                    <td>{p.supportingSignalCount}</td>
                    <td>
                      <span className={patchPillClass(p.status)}>
                        {PATCH_STATUS_LABEL[p.status] ?? p.status}
                      </span>
                    </td>
                    <td>
                      {p.status === McpDocPatchStatus.Proposed && (
                        <div style={{ display: 'flex', gap: 4 }}>
                          <button type="button" className="btn btn--sm btn--primary" onClick={() => handleApply(p.id)}>
                            Apply
                          </button>
                          <button type="button" className="btn btn--sm btn--danger" onClick={() => handleReject(p.id)}>
                            Reject
                          </button>
                        </div>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )
        )}

        {tab === 'problems' && (
          stats && stats.problemTables.length === 0 ? (
            <EmptyState title="No problem tables" description="No tables with high error rates detected." />
          ) : (
            <table className="table">
              <thead>
                <tr>
                  <th>Tables</th>
                  <th>Total queries</th>
                  <th>Errors</th>
                  <th>Error rate</th>
                </tr>
              </thead>
              <tbody>
                {stats?.problemTables.map((p, i) => (
                  <tr key={i}>
                    <td>{p.tablesUsed}</td>
                    <td>{p.totalQueries}</td>
                    <td>{p.errorCount}</td>
                    <td style={{ color: p.errorRate >= 0.5 ? 'var(--crit)' : 'var(--warn)' }}>
                      {(p.errorRate * 100).toFixed(0)}%
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )
        )}
      </div>
    </div>
  );
}

function StatCard({
  label,
  value,
  sub,
  tone,
}: {
  label: string;
  value: React.ReactNode;
  sub?: string;
  tone?: 'ok' | 'warn' | 'crit';
}) {
  const color =
    tone === 'ok' ? 'var(--ok)' : tone === 'warn' ? 'var(--warn)' : tone === 'crit' ? 'var(--crit)' : 'var(--text)';
  return (
    <div className="card" style={{ padding: 12 }}>
      <div className="muted" style={{ fontSize: 11 }}>{label}</div>
      <div style={{ fontWeight: 700, fontSize: 22, color, marginTop: 4 }}>{value}</div>
      {sub && <div className="muted" style={{ fontSize: 11 }}>{sub}</div>}
    </div>
  );
}

function statusPillClass(status: number): string {
  if (status === McpPatternStatus.Approved || status === McpPatternStatus.AutoApproved) return 'pill pill--ok';
  if (status === McpPatternStatus.Pending) return 'pill pill--warn';
  return 'pill pill--crit';
}

function patchPillClass(status: number): string {
  if (status === McpDocPatchStatus.Applied) return 'pill pill--ok';
  if (status === McpDocPatchStatus.Proposed) return 'pill pill--info';
  return 'pill pill--crit';
}
