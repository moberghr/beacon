import { useState } from 'react';
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
import { Pager } from '@/components/data/Pager';
import { EmptyState } from '@/components/data/EmptyState';
import { Tabs, type TabDef } from '@/components/Tabs';
import { useIsAdmin } from '@/auth/useAuth';
import { McpDocPatchStatus, McpPatternStatus } from '@/lib/enums';
import { GoldenCandidatesTab, GoldenCasesTab } from './GoldenTabs';
import { GroundingRecheckTab } from './GroundingRecheckTab';
import { PatchDetailModal, PatternDetailModal, UNKNOWN_REVIEWER } from './LearningDetailModals';
import {
  PATCH_STATUS_LABEL,
  PATCH_TARGET_LABEL,
  PATTERN_CURATE_REASON,
  PATTERN_REVIEW_ACTIONS,
  PATTERN_STATUS_LABEL,
  PATTERN_TYPE_LABEL,
  isChangedSinceReviewed,
  patchCurateReason,
  patternWithheldReasons,
  useApplyPatch,
  useDocumentationPatchesList,
  useLearnedPatternsList,
  useLearningStats,
  useRejectPatch,
  useUpdatePatternStatus,
  type DocumentationPatchEntry,
  type LearnedPatternEntry,
} from './queries';

type TabKey = 'patterns' | 'patches' | 'problems' | 'candidates' | 'golden' | 'recheck';

const ADMIN_TABS: TabKey[] = ['candidates', 'golden', 'recheck'];

export default function McpLearningPage() {
  const statsQ = useLearningStats();
  const patternsQ = useLearnedPatternsList();
  const patchesQ = useDocumentationPatchesList();
  const updatePattern = useUpdatePatternStatus();
  const applyPatch = useApplyPatch();
  const rejectPatch = useRejectPatch();
  const isAdmin = useIsAdmin() === true;

  const [tab, setTab] = useState<TabKey>('patterns');
  const [viewingPattern, setViewingPattern] = useState<LearnedPatternEntry | null>(null);
  const [viewingPatch, setViewingPatch] = useState<DocumentationPatchEntry | null>(null);
  const statusFilter = patternsQ.filters.status;
  const typeFilter = patternsQ.filters.patternType;

  const stats = statsQ.data;
  const filteredPatterns = patternsQ.items;
  const patches = patchesQ.items;

  // Error toasts come from the mutation hook (single-toast rule); a pattern that changed since it was reviewed is read
  // again, so its detail view closes rather than offer a decision on what the reviewer no longer sees.
  function handleReviewPattern(pattern: LearnedPatternEntry, status: number) {
    const done = PATTERN_REVIEW_ACTIONS.find(x => x.status === status)?.done ?? 'Pattern updated.';
    updatePattern.mutate(
      { patternId: pattern.id, newStatus: status, reviewedContentHash: pattern.contentHash },
      {
        onSuccess: () => {
          setViewingPattern(null);
          toast.success(done);
        },
        onError: err => {
          if (isChangedSinceReviewed(err)) {
            setViewingPattern(null);
          }
        },
      },
    );
  }

  function handleApply(patch: DocumentationPatchEntry) {
    applyPatch.mutate(patch.id, {
      onSuccess: () => {
        setViewingPatch(null);
        toast.success('Patch applied.');
      },
    });
  }

  function handleReject(patch: DocumentationPatchEntry) {
    rejectPatch.mutate(patch.id, {
      onSuccess: () => {
        setViewingPatch(null);
        toast.success('Patch rejected.');
      },
    });
  }

  const tabs: TabDef<TabKey>[] = [
    {
      key: 'patterns',
      label: 'Learned patterns',
      count: stats?.patternsPending,
    },
    { key: 'patches', label: 'Doc patches', count: stats?.patchesProposed },
    { key: 'problems', label: 'Problem tables' },
  ];
  if (isAdmin) {
    tabs.push(
      { key: 'candidates', label: 'Golden candidates' },
      { key: 'golden', label: 'Golden examples' },
      { key: 'recheck', label: 'Grounding recheck' },
    );
  }
  const activeTab: TabKey = !isAdmin && ADMIN_TABS.includes(tab) ? 'patterns' : tab;

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

      <Tabs<TabKey> active={activeTab} onChange={setTab} tabs={tabs} />

      <Card className="p-4">
        {activeTab === 'patterns' && (
          <>
            <div className="flex gap-2 mb-3">
              <Select value={statusFilter} onChange={e => patternsQ.setFilter('status', e.target.value)}>
                <option value="">All statuses</option>
                {Object.entries(PATTERN_STATUS_LABEL).map(([k, label]) => (
                  <option key={k} value={k}>{label}</option>
                ))}
              </Select>
              <Select value={typeFilter} onChange={e => patternsQ.setFilter('patternType', e.target.value)}>
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
              <>
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
                        <div className="flex flex-wrap items-center gap-1">
                          <Pill tone={patternTone(p.status)}>
                            {PATTERN_STATUS_LABEL[p.status] ?? p.status}
                          </Pill>
                          {patternWithheldReasons(p).length > 0 && (
                            <span title={patternWithheldReasons(p).join(' ')}>
                              <Pill tone="crit">Example left out of prompts</Pill>
                            </span>
                          )}
                        </div>
                      </td>
                      <td className="px-2 py-2">
                        <div className="flex flex-wrap items-center gap-1">
                          <Button size="sm" type="button" onClick={() => setViewingPattern(p)}>
                            View
                          </Button>
                          {p.canCurate ? (
                            PATTERN_REVIEW_ACTIONS.filter(x => x.status !== p.status).map(x => (
                              <Button
                                key={x.status}
                                variant={x.status === McpPatternStatus.Approved ? 'primary' : x.status === McpPatternStatus.Rejected ? 'danger' : 'secondary'}
                                size="sm"
                                type="button"
                                disabled={updatePattern.isPending}
                                onClick={() => handleReviewPattern(p, x.status)}
                              >
                                {x.label}
                              </Button>
                            ))
                          ) : (
                            <span className="text-2xs text-text-muted" title={PATTERN_CURATE_REASON}>
                              Project admins review
                            </span>
                          )}
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
              <Pager {...patternsQ.tableProps.paging} />
              </>
            )}
          </>
        )}

        {activeTab === 'patches' && (
          patchesQ.isLoading ? (
            <p className="text-text-muted">Loading patches…</p>
          ) : patches.length === 0 ? (
            <EmptyState title="No documentation patches" />
          ) : (
            <>
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
                    <td className="px-2 py-2">{PATCH_TARGET_LABEL[p.targetType] ?? p.targetType}</td>
                    <td className="px-2 py-2">{p.targetIdentifier}</td>
                    <td className="px-2 py-2 text-text-muted max-w-[220px] truncate" title={p.currentContent ?? undefined}>
                      {p.currentContent ?? '—'}
                    </td>
                    <td className="px-2 py-2 max-w-[220px] truncate" title={p.proposedContent}>
                      {p.proposedContent}
                    </td>
                    <td className="px-2 py-2">{p.supportingSignalCount}</td>
                    <td className="px-2 py-2">
                      <Pill tone={patchTone(p.status)}>
                        {PATCH_STATUS_LABEL[p.status] ?? p.status}
                      </Pill>
                      {p.status !== McpDocPatchStatus.Proposed && (p.appliedByName || p.appliedAt) && (
                        <div className="mt-0.5 text-2xs text-text-muted">by {p.appliedByName ?? UNKNOWN_REVIEWER}</div>
                      )}
                    </td>
                    <td className="px-2 py-2">
                      <div className="flex flex-wrap items-center gap-1">
                        <Button size="sm" type="button" onClick={() => setViewingPatch(p)}>
                          View
                        </Button>
                        {p.status === McpDocPatchStatus.Proposed && (
                          p.canCurate ? (
                            <>
                              <Button
                                variant="primary"
                                size="sm"
                                type="button"
                                disabled={applyPatch.isPending || rejectPatch.isPending}
                                onClick={() => handleApply(p)}
                              >
                                Apply
                              </Button>
                              <Button
                                variant="danger"
                                size="sm"
                                type="button"
                                disabled={applyPatch.isPending || rejectPatch.isPending}
                                onClick={() => handleReject(p)}
                              >
                                Reject
                              </Button>
                            </>
                          ) : (
                            <span className="text-2xs text-text-muted" title={patchCurateReason(p.targetType)}>
                              Curators apply
                            </span>
                          )
                        )}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            <Pager {...patchesQ.tableProps.paging} />
            </>
          )
        )}

        {activeTab === 'problems' && (
          statsQ.isLoading ? (
            <p className="text-text-muted">Loading problem tables…</p>
          ) : !stats ? (
            <EmptyState
              title="Stats unavailable"
              description="Learning stats failed to load — retry above."
            />
          ) : stats.problemTables.length === 0 ? (
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
                {stats.problemTables.map((p, i) => (
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

        {activeTab === 'candidates' && <GoldenCandidatesTab />}
        {activeTab === 'golden' && <GoldenCasesTab />}
        {activeTab === 'recheck' && <GroundingRecheckTab />}
      </Card>

      <PatternDetailModal
        pattern={viewingPattern}
        onClose={() => setViewingPattern(null)}
        onReview={handleReviewPattern}
        isBusy={updatePattern.isPending}
      />
      <PatchDetailModal
        patch={viewingPatch}
        onClose={() => setViewingPatch(null)}
        onApply={handleApply}
        onReject={handleReject}
        isBusy={applyPatch.isPending || rejectPatch.isPending}
      />
    </div>
  );
}

function patternTone(status: number): PillProps['tone'] {
  if (status === McpPatternStatus.Approved || status === McpPatternStatus.AutoApproved) return 'ok';
  if (status === McpPatternStatus.Pending || status === McpPatternStatus.NeedsEvidence) return 'warn';
  return 'crit';
}

function patchTone(status: number): PillProps['tone'] {
  if (status === McpDocPatchStatus.Applied) return 'ok';
  if (status === McpDocPatchStatus.Proposed) return 'info';
  return 'crit';
}
