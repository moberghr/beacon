import { useState } from 'react';
import { toast } from 'sonner';
import { Button, Pill } from '@/components/beacon';
import { EmptyState } from '@/components/data/EmptyState';
import { GroundingRecheckKind, McpPatternStatus } from '@/lib/enums';
import { showHiddenCharacters } from '@/lib/hiddenCharacters';
import { ProjectPicker } from '../projects/ProjectPicker';
import { ConfirmDeactivateDialog } from './GoldenTabs';
import {
  PATTERN_STATUS_LABEL,
  RECHECK_KIND_LABEL,
  useDeactivateGoldenCase,
  useGroundingRecheck,
  useUpdatePatternStatus,
  type GroundingRecheckItem,
} from './queries';

/**
 * Admin only: checks a project's active golden cases and approved learned patterns against today's grounding checks and
 * lists the ones refused, with the reasons. Nothing is rewritten: per row the Admin deactivates a golden case or sets a
 * pattern back to pending, and the recheck runs again.
 */
export function GroundingRecheckTab() {
  const [project, setProject] = useState<{ id: number; name: string } | null>(null);
  const [ranFor, setRanFor] = useState<number | null>(null);
  const projectId = project?.id ?? null;
  const recheck = useGroundingRecheck(projectId, projectId !== null && ranFor === projectId);
  const updatePattern = useUpdatePatternStatus();
  const deactivate = useDeactivateGoldenCase();
  const [deactivating, setDeactivating] = useState<GroundingRecheckItem | null>(null);

  function run() {
    if (projectId === null) return;
    if (ranFor === projectId) {
      void recheck.refetch();
      return;
    }
    setRanFor(projectId);
  }

  // Error toasts come from the mutation hooks; a pattern that changed since the recheck read it is refused (409).
  function backToPending(item: GroundingRecheckItem) {
    updatePattern.mutate(
      { patternId: item.id, newStatus: McpPatternStatus.Pending, reviewedContentHash: item.contentHash ?? '' },
      { onSuccess: () => toast.success(`Pattern #${item.id} set back to pending.`) },
    );
  }

  function confirmDeactivate() {
    if (deactivating === null) return;
    deactivate.mutate(deactivating.id, { onSuccess: () => setDeactivating(null) });
  }

  const result = recheck.data;
  const isBusy = updatePattern.isPending || deactivate.isPending;

  return (
    <>
      <p className="m-0 mb-3 text-sm text-text-muted">
        Checks a project&apos;s active golden cases and approved learned patterns against today&apos;s grounding checks.
        Nothing is changed: you decide what to do with each row it refuses.
      </p>
      <div className="mb-3 flex gap-2">
        <ProjectPicker
          value={projectId}
          selectedLabel={project?.name}
          ariaLabel="Recheck project"
          placeholder="Choose a project…"
          onSelect={x => setProject(x ? { id: x.id, name: x.name } : null)}
        />
        <Button variant="primary" type="button" disabled={projectId === null || recheck.isFetching} onClick={run}>
          {recheck.isFetching ? 'Checking…' : 'Run recheck'}
        </Button>
      </div>
      {projectId === null ? (
        <EmptyState title="Choose a project" description="The recheck runs over one project's grounding." />
      ) : ranFor !== projectId ? (
        <EmptyState title="Not checked yet" description="Run the recheck to list what today's checks refuse." />
      ) : recheck.isLoading ? (
        <p className="text-text-muted">Checking grounding…</p>
      ) : recheck.isError ? (
        <EmptyState
          title="The recheck failed"
          description={recheck.error instanceof Error ? recheck.error.message : 'Unknown error'}
        />
      ) : result ? (
        <>
          <p className="m-0 mb-3 text-sm">
            Checked {result.goldenCasesChecked} active golden {result.goldenCasesChecked === 1 ? 'case' : 'cases'} and{' '}
            {result.patternsChecked} approved learned {result.patternsChecked === 1 ? 'pattern' : 'patterns'}.
          </p>
          {result.refused.length === 0 ? (
            <EmptyState title="Nothing refused" description="Today's checks pass every golden case and pattern in use." />
          ) : (
            <table className="w-full text-sm">
              <thead>
                <tr className="text-left text-2xs font-semibold uppercase tracking-eyebrow text-text-muted border-b border-border">
                  <th className="px-2 py-2">Kind</th>
                  <th className="px-2 py-2">Question</th>
                  <th className="px-2 py-2">Refused</th>
                  <th className="px-2 py-2">Actions</th>
                </tr>
              </thead>
              <tbody>
                {result.refused.map(x => (
                  <tr key={`${x.kind}-${x.id}`} className="border-b border-border last:border-b-0 align-top">
                    <td className="px-2 py-2 whitespace-nowrap">
                      <div>
                        {RECHECK_KIND_LABEL[x.kind] ?? x.kind} #{x.id}
                      </div>
                      {x.kind === GroundingRecheckKind.LearnedPattern && x.patternStatus !== null && (
                        <Pill tone="ok">{PATTERN_STATUS_LABEL[x.patternStatus] ?? x.patternStatus}</Pill>
                      )}
                      {x.kind === GroundingRecheckKind.GoldenCase && x.promotedByUserId === null && (
                        <div className="text-2xs text-text-muted">Promoted before attribution</div>
                      )}
                    </td>
                    <td className="px-2 py-2 max-w-[280px] truncate" title={showHiddenCharacters(x.question ?? '')}>
                      {x.question ? showHiddenCharacters(x.question) : '—'}
                    </td>
                    <td className="px-2 py-2 max-w-[420px]">
                      <ul className="m-0 flex list-none flex-col gap-1 p-0">
                        {x.questionRefusal && (
                          <li>
                            <span className="font-semibold">Question:</span> {x.questionRefusal}
                          </li>
                        )}
                        {x.sqlRefusal && (
                          <li>
                            <span className="font-semibold">SQL:</span> {x.sqlRefusal}
                          </li>
                        )}
                      </ul>
                    </td>
                    <td className="px-2 py-2">
                      {x.kind === GroundingRecheckKind.GoldenCase ? (
                        <Button variant="danger" size="sm" type="button" disabled={isBusy} onClick={() => setDeactivating(x)}>
                          Deactivate
                        </Button>
                      ) : (
                        <Button size="sm" type="button" disabled={isBusy} onClick={() => backToPending(x)}>
                          Back to pending
                        </Button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </>
      ) : null}

      <ConfirmDeactivateDialog
        goldenCase={deactivating}
        busy={deactivate.isPending}
        onConfirm={confirmDeactivate}
        onCancel={() => setDeactivating(null)}
      />
    </>
  );
}
