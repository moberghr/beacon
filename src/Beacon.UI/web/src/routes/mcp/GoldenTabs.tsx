import { useState, type ReactNode } from 'react';
import { toast } from 'sonner';
import {
  Banner,
  Button,
  Kbd,
  Modal,
  ModalBody,
  ModalFooter,
  ModalHeader,
  Pill,
} from '@/components/beacon';
import { EmptyState } from '@/components/data/EmptyState';
import { Pager } from '@/components/data/Pager';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { formatDateTime } from '@/lib/format';
import { showHiddenCharacters } from '@/lib/hiddenCharacters';
import { ProjectPicker } from '../projects/ProjectPicker';
import { ContentBlock, Fact } from './LearningDetailModals';
import {
  goldenWithheldReasons,
  isChangedSinceReviewed,
  useDeactivateGoldenCase,
  useGoldenCandidatesList,
  useGoldenCases,
  usePromoteCandidate,
  type GoldenCandidateItem,
  type GoldenCaseItem,
} from './queries';

const closeHint = (
  <>
    <Kbd>Esc</Kbd> close
  </>
);

const NOT_RECORDED = 'Not recorded';

/**
 * Admin only. The answers rated correct that no golden case was promoted from yet, newest first; feedback never makes
 * a golden example by itself, an Admin promotes it here. Promoting is idempotent: a candidate someone else promoted
 * meanwhile answers with the existing case, which is shown instead of a new one.
 */
export function GoldenCandidatesTab() {
  const candidates = useGoldenCandidatesList(true);
  const promote = usePromoteCandidate();
  const [projectLabel, setProjectLabel] = useState<string | null>(null);
  const [viewing, setViewing] = useState<GoldenCandidateItem | null>(null);
  const [promoted, setPromoted] = useState<{ projectId: number; caseId: number; alreadyPromoted: boolean } | null>(null);

  const projectFilter = candidates.filters.projectId;
  const projectId = projectFilter === '' ? null : Number(projectFilter);

  // A candidate that changed since it was reviewed is read again (the hook says so), so its detail view closes.
  function handlePromote(candidate: GoldenCandidateItem) {
    promote.mutate({ signalId: candidate.signalId, reviewedSqlHash: candidate.sqlHash }, {
      onSuccess: result => {
        setViewing(null);
        if (!result.alreadyPromoted) {
          toast.success(`Promoted to golden case #${result.evalCaseId}.`);
        }
        setPromoted({ projectId: candidate.projectId, caseId: result.evalCaseId, alreadyPromoted: result.alreadyPromoted });
      },
      onError: err => {
        if (isChangedSinceReviewed(err)) {
          setViewing(null);
        }
      },
    });
  }

  return (
    <>
      <p className="m-0 mb-3 text-sm text-text-muted">
        Answers rated correct through feedback wait here; none grounds SQL generation until an Admin promotes it.
      </p>
      <div className="mb-3 flex gap-2">
        <ProjectPicker
          value={projectId}
          selectedLabel={projectLabel ?? undefined}
          clearLabel="All projects"
          ariaLabel="Candidates project"
          onSelect={x => {
            setProjectLabel(x?.name ?? null);
            candidates.setFilter('projectId', x ? String(x.id) : '');
          }}
        />
      </div>
      {candidates.isLoading ? (
        <p className="text-text-muted">Loading candidates…</p>
      ) : candidates.isError ? (
        <EmptyState
          title="Failed to load golden candidates"
          description={candidates.error instanceof Error ? candidates.error.message : 'Unknown error'}
        />
      ) : candidates.items.length === 0 ? (
        <EmptyState title="No golden candidates" description="No answer rated correct is waiting to be promoted." />
      ) : (
        <>
          <table className="w-full text-sm">
            <thead>
              <tr className="text-left text-2xs font-semibold uppercase tracking-eyebrow text-text-muted border-b border-border">
                <th className="px-2 py-2">Question</th>
                <th className="px-2 py-2">SQL</th>
                <th className="px-2 py-2">Asked by</th>
                <th className="px-2 py-2">Rated by</th>
                <th className="px-2 py-2">Asked</th>
                <th className="px-2 py-2">Actions</th>
              </tr>
            </thead>
            <tbody>
              {candidates.items.map(x => (
                <tr key={x.signalId} className="border-b border-border last:border-b-0">
                  <td className="px-2 py-2 max-w-[260px] truncate" title={showHiddenCharacters(x.question)}>
                    {showHiddenCharacters(x.question)}
                  </td>
                  <td className="px-2 py-2 max-w-[260px]">
                    <div className="flex items-center gap-1.5">
                      <span className="mono truncate text-xs" title={showHiddenCharacters(x.sql)}>{showHiddenCharacters(x.sql)}</span>
                      {x.isUserCorrected && <Pill tone="info">Corrected</Pill>}
                    </div>
                  </td>
                  <td className="px-2 py-2">{x.askedByName ?? NOT_RECORDED}</td>
                  <td className="px-2 py-2">{x.ratedByName ?? NOT_RECORDED}</td>
                  <td className="px-2 py-2 whitespace-nowrap">{formatDateTime(x.createdTime)}</td>
                  <td className="px-2 py-2">
                    <div className="flex gap-1">
                      <Button size="sm" type="button" onClick={() => setViewing(x)}>
                        View
                      </Button>
                      <Button
                        variant="primary"
                        size="sm"
                        type="button"
                        disabled={promote.isPending}
                        onClick={() => handlePromote(x)}
                      >
                        Promote
                      </Button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <Pager {...candidates.tableProps.paging} />
        </>
      )}

      {viewing && (
        <Modal open onClose={() => setViewing(null)} width={760} ariaLabel="Golden candidate">
          <ModalHeader eyebrow="Golden candidate" prefix="Rated" emphasis="correct" onClose={() => setViewing(null)} />
          <ModalBody className="flex flex-col gap-4">
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-3">
              <Fact label="Asked by">{viewing.askedByName ?? NOT_RECORDED}</Fact>
              <Fact label="Rated by">{viewing.ratedByName ?? NOT_RECORDED}</Fact>
              <Fact label="Asked">{formatDateTime(viewing.createdTime)}</Fact>
            </div>
            <ContentBlock label="Question" text={viewing.question} />
            <ContentBlock label={viewing.isUserCorrected ? 'SQL (corrected by the rater)' : 'SQL'} text={viewing.sql} mono />
            <ContentBlock label="Note" text={viewing.feedbackNote} />
            <p className="m-0 text-xs text-text-muted">
              Promoting checks the SQL again and stores it without comments.
            </p>
          </ModalBody>
          <ModalFooter hints={closeHint}>
            <Button variant="primary" size="sm" type="button" disabled={promote.isPending} onClick={() => handlePromote(viewing)}>
              Promote
            </Button>
          </ModalFooter>
        </Modal>
      )}

      {promoted && (
        <PromotedCaseModal
          projectId={promoted.projectId}
          caseId={promoted.caseId}
          notice={
            promoted.alreadyPromoted ? (
              <Banner
                tone="info"
                title="This answer was already promoted"
                sub={`Nothing new was created: it is golden case #${promoted.caseId}.`}
              />
            ) : null
          }
          onClose={() => setPromoted(null)}
        />
      )}
    </>
  );
}

/** The case a promotion answered with, read from its project's golden cases. */
function PromotedCaseModal({
  projectId,
  caseId,
  notice,
  onClose,
}: {
  projectId: number;
  caseId: number;
  notice: ReactNode;
  onClose: () => void;
}) {
  const cases = useGoldenCases(projectId);
  const goldenCase = cases.data?.find(x => x.id === caseId) ?? null;

  return (
    <GoldenCaseModal
      goldenCase={goldenCase}
      caseId={caseId}
      isLoading={cases.isLoading}
      notice={notice}
      onClose={onClose}
    />
  );
}

/**
 * Admin only: a project's golden cases, with who promoted and who last edited each, which ones prompts leave out, and a
 * Deactivate action for an active case (prompts stop using it; it stays listed).
 */
export function GoldenCasesTab() {
  const [project, setProject] = useState<{ id: number; name: string } | null>(null);
  const cases = useGoldenCases(project?.id ?? null);
  const [viewing, setViewing] = useState<GoldenCaseItem | null>(null);
  const [deactivating, setDeactivating] = useState<GoldenCaseItem | null>(null);
  const deactivate = useDeactivateGoldenCase();

  function confirmDeactivate() {
    if (deactivating === null) return;
    deactivate.mutate(deactivating.id, {
      onSuccess: () => {
        setDeactivating(null);
        setViewing(null);
      },
    });
  }

  return (
    <>
      <div className="mb-3 flex gap-2">
        <ProjectPicker
          value={project?.id ?? null}
          selectedLabel={project?.name}
          ariaLabel="Golden examples project"
          placeholder="Choose a project…"
          onSelect={x => setProject(x ? { id: x.id, name: x.name } : null)}
        />
      </div>
      {project === null ? (
        <EmptyState title="Choose a project" description="Golden examples are listed per project." />
      ) : cases.isLoading ? (
        <p className="text-text-muted">Loading golden examples…</p>
      ) : cases.isError ? (
        <EmptyState
          title="Failed to load golden examples"
          description={cases.error instanceof Error ? cases.error.message : 'Unknown error'}
        />
      ) : (cases.data ?? []).length === 0 ? (
        <EmptyState title="No golden examples" description="An Admin promotes golden candidates into this project." />
      ) : (
        <table className="w-full text-sm">
          <thead>
            <tr className="text-left text-2xs font-semibold uppercase tracking-eyebrow text-text-muted border-b border-border">
              <th className="px-2 py-2">Question</th>
              <th className="px-2 py-2">State</th>
              <th className="px-2 py-2">Promoted by</th>
              <th className="px-2 py-2">Last edited by</th>
              <th className="px-2 py-2">Created</th>
              <th className="px-2 py-2">Actions</th>
            </tr>
          </thead>
          <tbody>
            {(cases.data ?? []).map(x => (
              <tr key={x.id} className="border-b border-border last:border-b-0">
                <td className="px-2 py-2 max-w-[320px] truncate" title={showHiddenCharacters(x.question)}>
                  {showHiddenCharacters(x.question)}
                </td>
                <td className="px-2 py-2">
                  <div className="flex flex-wrap items-center gap-1">
                    <Pill tone={x.isActive ? 'ok' : 'warn'}>{x.isActive ? 'Active' : 'Inactive'}</Pill>
                    {goldenWithheldReasons(x).length > 0 && (
                      <span title={goldenWithheldReasons(x).join(' ')}>
                        <Pill tone="crit">Left out of prompts</Pill>
                      </span>
                    )}
                  </div>
                </td>
                <td className="px-2 py-2">{x.promotedByName ?? NOT_RECORDED}</td>
                <td className="px-2 py-2">{x.modifiedByName ?? '—'}</td>
                <td className="px-2 py-2 whitespace-nowrap">{formatDateTime(x.createdTime)}</td>
                <td className="px-2 py-2">
                  <div className="flex gap-1">
                    <Button size="sm" type="button" onClick={() => setViewing(x)}>
                      View
                    </Button>
                    {x.isActive && (
                      <Button
                        variant="danger"
                        size="sm"
                        type="button"
                        disabled={deactivate.isPending}
                        onClick={() => setDeactivating(x)}
                      >
                        Deactivate
                      </Button>
                    )}
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {viewing && (
        <GoldenCaseModal
          goldenCase={viewing}
          caseId={viewing.id}
          isLoading={false}
          notice={null}
          onClose={() => setViewing(null)}
          onDeactivate={viewing.isActive ? () => setDeactivating(viewing) : undefined}
          isBusy={deactivate.isPending}
        />
      )}

      <ConfirmDeactivateDialog
        goldenCase={deactivating}
        busy={deactivate.isPending}
        onConfirm={confirmDeactivate}
        onCancel={() => setDeactivating(null)}
      />
    </>
  );
}

/** Asks before a golden case is deactivated: prompts stop using it, and it stays listed. */
export function ConfirmDeactivateDialog({
  goldenCase,
  busy,
  onConfirm,
  onCancel,
}: {
  goldenCase: { id: number } | null;
  busy: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  return (
    <ConfirmDialog
      open={goldenCase !== null}
      title="Deactivate golden case"
      message={
        goldenCase
          ? <>Deactivate golden case <strong>#{goldenCase.id}</strong>? Prompts stop using it as an example; it stays listed and can be edited.</>
          : ''
      }
      confirmLabel="Deactivate"
      destructive
      busy={busy}
      onConfirm={onConfirm}
      onCancel={onCancel}
    />
  );
}

/**
 * A golden case in full: question, gold SQL, notes, who promoted it and who last edited it, and why prompts leave it out
 * when they do; with `onDeactivate`, the Deactivate action.
 */
function GoldenCaseModal({
  goldenCase,
  caseId,
  isLoading,
  notice,
  onClose,
  onDeactivate,
  isBusy = false,
}: {
  goldenCase: GoldenCaseItem | null;
  caseId: number;
  isLoading: boolean;
  notice: ReactNode;
  onClose: () => void;
  onDeactivate?: () => void;
  isBusy?: boolean;
}) {
  const withheld = goldenCase === null ? [] : goldenWithheldReasons(goldenCase);
  return (
    <Modal open onClose={onClose} width={760} ariaLabel="Golden case">
      <ModalHeader eyebrow="Golden example" prefix="Case" emphasis={`#${caseId}`} onClose={onClose} />
      <ModalBody className="flex flex-col gap-4">
        {notice}
        {goldenCase === null ? (
          <p className="m-0 text-sm text-text-muted">{isLoading ? 'Loading the case…' : 'The case is not in this project’s list.'}</p>
        ) : (
          <>
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
              <Fact label="State">
                <Pill tone={goldenCase.isActive ? 'ok' : 'warn'}>{goldenCase.isActive ? 'Active' : 'Inactive'}</Pill>
              </Fact>
              <Fact label="Promoted by">{goldenCase.promotedByName ?? NOT_RECORDED}</Fact>
              <Fact label="Last edited by">{goldenCase.modifiedByName ?? '—'}</Fact>
              <Fact label="Created">{formatDateTime(goldenCase.createdTime)}</Fact>
            </div>
            {withheld.length > 0 && (
              <Banner tone="warn" title="Prompts leave this case out" sub={`${withheld.join(' ')} Edit it, or deactivate it.`} />
            )}
            <ContentBlock label="Question" text={goldenCase.question} />
            <ContentBlock label="Gold SQL" text={goldenCase.goldSql} mono />
            <ContentBlock label="Notes" text={goldenCase.notes} />
          </>
        )}
      </ModalBody>
      <ModalFooter hints={closeHint}>
        {onDeactivate && (
          <Button variant="danger" size="sm" type="button" disabled={isBusy} onClick={onDeactivate}>
            Deactivate
          </Button>
        )}
        <Button size="sm" type="button" onClick={onClose}>
          Close
        </Button>
      </ModalFooter>
    </Modal>
  );
}
