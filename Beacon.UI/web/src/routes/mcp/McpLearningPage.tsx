import { useMemo, useState } from 'react';
import { toast } from 'sonner';
import { AlertTriangle } from 'lucide-react';
import {
  PageHeader,
  Button,
  Card,
  KPI,
  KPIGrid,
  Pill,
  Select,
  type PillProps,
} from '@/components/beacon';
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
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        variant="signal"
        eyebrow="MCP"
        prefix="Learning from"
        emphasis="usage"
        sub="The MCP server learns from every query. Review patterns, manage documentation improvements."
      />

      {statsQ.isLoading && <p className="text-text-muted">Loading stats…</p>}
      {statsQ.isError && (
        <EmptyState
          icon={<AlertTriangle size={20} />}
          title="Failed to load learning stats"
          description={statsQ.error instanceof Error ? statsQ.error.message : 'Unknown error'}
          action={
            <Button variant="primary" onClick={() => statsQ.refetch()}>
              Retry
            </Button>
          }
        />
      )}
      {stats && (
        <KPIGrid>
          <KPI dot="brand" label="Signals (7d / 30d)" value={`${stats.signals7d} / ${stats.signals30d}`} />
          <KPI
            dot={stats.successRate >= 0.8 ? 'ok' : stats.successRate >= 0.5 ? 'warn' : 'crit'}
            label="Success rate"
            value={`${(stats.successRate * 100).toFixed(0)}%`}
          />
          <KPI
            dot="ok"
            label="Patterns approved"
            value={stats.patternsApproved}
            sub={stats.patternsPending > 0 ? `${stats.patternsPending} pending` : undefined}
          />
          <KPI
            dot="info"
            label="Doc patches applied"
            value={stats.patchesApplied}
            sub={stats.patchesProposed > 0 ? `${stats.patchesProposed} proposed` : undefined}
          />
        </KPIGrid>
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

      <Card className="p-4">
        {tab === 'patterns' && (
          <>
            <div className="flex gap-2 mb-3">
              <Select value={statusFilter} onChange={e => setStatusFilter(e.target.value)}>
                <option value="">All statuses</option>
                {Object.entries(PATTERN_STATUS_LABEL).map(([k, label]) => (
                  <option key={k} value={k}>{label}</option>
                ))}
              </Select>
              <Select value={typeFilter} onChange={e => setTypeFilter(e.target.value)}>
                <option value="">All types</option>
                {Object.entries(PATTERN_TYPE_LABEL).map(([k, label]) => (
                  <option key={k} value={k}>{label}</option>
                ))}
              </Select>
            </div>
            {patternsQ.isLoading ? (
              <p className="text-text-muted">Loading patterns…</p>
            ) : filteredPatterns.length === 0 ? (
              <EmptyState title="No patterns" description="No learned patterns match the current filters." />
            ) : (
              <table className="w-full text-sm">
                <thead>
                  <tr className="text-left text-2xs font-semibold uppercase tracking-eyebrow text-text-muted border-b border-border">
                    <th className="px-2 py-2">Type</th>
                    <th className="px-2 py-2">Table</th>
                    <th className="px-2 py-2">Column</th>
                    <th className="px-2 py-2">Content</th>
                    <th className="px-2 py-2">Conf.</th>
                    <th className="px-2 py-2">Signals</th>
                    <th className="px-2 py-2">Status</th>
                    <th className="px-2 py-2">Actions</th>
                  </tr>
                </thead>
                <tbody>
                  {filteredPatterns.map(p => (
                    <tr key={p.id} className="border-b border-border last:border-b-0">
                      <td className="px-2 py-2">{PATTERN_TYPE_LABEL[p.patternType] ?? p.patternType}</td>
                      <td className="px-2 py-2">{p.tableName}</td>
                      <td className="px-2 py-2">{p.columnName ?? '—'}</td>
                      <td className="px-2 py-2 max-w-[300px] truncate" title={p.patternContent}>
                        {p.patternContent}
                      </td>
                      <td className="px-2 py-2">{(p.confidence * 100).toFixed(0)}%</td>
                      <td className="px-2 py-2">{p.signalCount}</td>
                      <td className="px-2 py-2">
                        <Pill tone={patternTone(p.status)}>
                          {PATTERN_STATUS_LABEL[p.status] ?? p.status}
                        </Pill>
                      </td>
                      <td className="px-2 py-2">
                        {p.status === McpPatternStatus.Pending && (
                          <div className="flex gap-1">
                            <Button
                              variant="primary"
                              size="sm"
                              type="button"
                              onClick={() => handleUpdatePattern(p.id, McpPatternStatus.Approved)}
                            >
                              Approve
                            </Button>
                            <Button
                              variant="danger"
                              size="sm"
                              type="button"
                              onClick={() => handleUpdatePattern(p.id, McpPatternStatus.Rejected)}
                            >
                              Reject
                            </Button>
                          </div>
                        )}
                        {p.status === McpPatternStatus.AutoApproved && (
                          <Button
                            variant="danger"
                            size="sm"
                            type="button"
                            onClick={() => handleUpdatePattern(p.id, McpPatternStatus.Rejected)}
                          >
                            Reject
                          </Button>
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
            <p className="text-text-muted">Loading patches…</p>
          ) : patches.length === 0 ? (
            <EmptyState title="No documentation patches" />
          ) : (
            <table className="w-full text-sm">
              <thead>
                <tr className="text-left text-2xs font-semibold uppercase tracking-eyebrow text-text-muted border-b border-border">
                  <th className="px-2 py-2">Target</th>
                  <th className="px-2 py-2">Identifier</th>
                  <th className="px-2 py-2">Current</th>
                  <th className="px-2 py-2">Proposed</th>
                  <th className="px-2 py-2">Signals</th>
                  <th className="px-2 py-2">Status</th>
                  <th className="px-2 py-2">Actions</th>
                </tr>
              </thead>
              <tbody>
                {patches.map(p => (
                  <tr key={p.id} className="border-b border-border last:border-b-0">
                    <td className="px-2 py-2">{p.targetType}</td>
                    <td className="px-2 py-2">{p.targetIdentifier}</td>
                    <td className="px-2 py-2 text-text-muted max-w-[220px] truncate">
                      {p.currentContent ?? '—'}
                    </td>
                    <td className="px-2 py-2 max-w-[220px] truncate">
                      {p.proposedContent}
                    </td>
                    <td className="px-2 py-2">{p.supportingSignalCount}</td>
                    <td className="px-2 py-2">
                      <Pill tone={patchTone(p.status)}>
                        {PATCH_STATUS_LABEL[p.status] ?? p.status}
                      </Pill>
                    </td>
                    <td className="px-2 py-2">
                      {p.status === McpDocPatchStatus.Proposed && (
                        <div className="flex gap-1">
                          <Button variant="primary" size="sm" type="button" onClick={() => handleApply(p.id)}>
                            Apply
                          </Button>
                          <Button variant="danger" size="sm" type="button" onClick={() => handleReject(p.id)}>
                            Reject
                          </Button>
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
            <table className="w-full text-sm">
              <thead>
                <tr className="text-left text-2xs font-semibold uppercase tracking-eyebrow text-text-muted border-b border-border">
                  <th className="px-2 py-2">Tables</th>
                  <th className="px-2 py-2">Total queries</th>
                  <th className="px-2 py-2">Errors</th>
                  <th className="px-2 py-2">Error rate</th>
                </tr>
              </thead>
              <tbody>
                {stats?.problemTables.map((p, i) => (
                  <tr key={i} className="border-b border-border last:border-b-0">
                    <td className="px-2 py-2">{p.tablesUsed}</td>
                    <td className="px-2 py-2">{p.totalQueries}</td>
                    <td className="px-2 py-2">{p.errorCount}</td>
                    <td className={`px-2 py-2 ${p.errorRate >= 0.5 ? 'text-crit' : 'text-warn'}`}>
                      {(p.errorRate * 100).toFixed(0)}%
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )
        )}
      </Card>
    </div>
  );
}

function patternTone(status: number): PillProps['tone'] {
  if (status === McpPatternStatus.Approved || status === McpPatternStatus.AutoApproved) return 'ok';
  if (status === McpPatternStatus.Pending) return 'warn';
  return 'crit';
}

function patchTone(status: number): PillProps['tone'] {
  if (status === McpDocPatchStatus.Applied) return 'ok';
  if (status === McpDocPatchStatus.Proposed) return 'info';
  return 'crit';
}
