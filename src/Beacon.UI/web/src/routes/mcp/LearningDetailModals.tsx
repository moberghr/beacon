import type { ReactNode } from 'react';
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
import { McpDocPatchStatus } from '@/lib/enums';
import { formatDateTime } from '@/lib/format';
import { hasHiddenCharacters, showHiddenCharacters } from '@/lib/hiddenCharacters';
import {
  PATCH_STATUS_LABEL,
  PATCH_TARGET_LABEL,
  PATTERN_CURATE_REASON,
  PATTERN_REVIEW_ACTIONS,
  PATTERN_STATUS_LABEL,
  PATTERN_TYPE_LABEL,
  patchCurateReason,
  patternWithheldReasons,
  type DocumentationPatchEntry,
  type LearnedPatternEntry,
} from './queries';

const closeHint = (
  <>
    <Kbd>Esc</Kbd> close
  </>
);

/**
 * A labelled block of full text: nothing is truncated in the detail views. Characters that do not show on screen are
 * shown as `\u{XXXX}`, and the block says so.
 */
export function ContentBlock({ label, text, mono = false }: { label: string; text: string | null; mono?: boolean }) {
  const hidden = hasHiddenCharacters(text);
  return (
    <section className="flex flex-col gap-1">
      <div className="flex items-center gap-2">
        <h3 className="eyebrow m-0">{label}</h3>
        {hidden && <Pill tone="warn">Hidden characters shown as {'\\u{…}'}</Pill>}
      </div>
      {text === null || text === '' ? (
        <p className="m-0 text-sm text-text-muted">—</p>
      ) : (
        <pre
          className={`m-0 whitespace-pre-wrap break-words rounded-xs border border-border bg-surface-2 p-2.5 text-[13px] ${mono ? 'mono' : ''}`}
        >
          {showHiddenCharacters(text)}
        </pre>
      )}
    </section>
  );
}

/** "Label: value" in the detail views' fact line. */
export function Fact({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-0.5">
      <span className="text-2xs uppercase tracking-eyebrow text-text-muted">{label}</span>
      <span className="text-sm">{children}</span>
    </div>
  );
}

/** What a decision made before Beacon recorded who made it shows: it has a time but no name. */
export const UNKNOWN_REVIEWER = 'Unknown reviewer';

/** Who made a decision and when; a decision recorded with a time but without a user says so instead of naming nobody. */
export function decidedBy(name: string | null, at: string | null): string {
  if (name && at) return `${name} · ${formatDateTime(at)}`;
  if (name) return name;
  if (at) return `${UNKNOWN_REVIEWER} · ${formatDateTime(at)}`;
  return 'Not recorded';
}

interface PatternDetailModalProps {
  pattern: LearnedPatternEntry | null;
  onClose: () => void;
  onReview: (pattern: LearnedPatternEntry, status: number) => void;
  isBusy: boolean;
  /** The MCP gateway serves no review: no actions and no review note. */
  gateway?: boolean;
}

/** A learned pattern in full — its content and example — with who reviewed it and the review actions the caller holds. */
export function PatternDetailModal({ pattern, onClose, onReview, isBusy, gateway = false }: PatternDetailModalProps) {
  if (pattern === null) return null;

  const target = [pattern.schemaName, pattern.tableName, pattern.columnName].filter(Boolean).join('.');
  const actions = PATTERN_REVIEW_ACTIONS.filter(x => x.status !== pattern.status);
  const withheld = patternWithheldReasons(pattern);

  return (
    <Modal open onClose={onClose} width={760} ariaLabel="Learned pattern">
      <ModalHeader
        eyebrow="Learned pattern"
        emphasis={PATTERN_TYPE_LABEL[pattern.patternType] ?? String(pattern.patternType)}
        suffix={<span className="mono text-base font-normal">{target}</span>}
        onClose={onClose}
      />
      <ModalBody className="flex flex-col gap-4">
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
          <Fact label="Status">
            <Pill tone="info">{PATTERN_STATUS_LABEL[pattern.status] ?? pattern.status}</Pill>
          </Fact>
          <Fact label="Confidence">{(pattern.confidence * 100).toFixed(0)}%</Fact>
          <Fact label="Signals">{pattern.signalCount}</Fact>
          <Fact label="Reviewed by">{decidedBy(pattern.reviewedByName, pattern.reviewedAt)}</Fact>
        </div>
        {withheld.length > 0 && (
          <Banner tone="warn" title="Prompts leave this example out (the lesson stays)" sub={withheld.join(' ')} />
        )}
        <ContentBlock label="Content" text={pattern.patternContent} />
        <ContentBlock label="Example question" text={pattern.exampleQuestion} />
        <ContentBlock label="Example SQL" text={pattern.exampleSql} mono />
        {!pattern.canCurate && !gateway && (
          <Banner tone="info" title="You cannot review this pattern" sub={PATTERN_CURATE_REASON} />
        )}
      </ModalBody>
      <ModalFooter hints={closeHint}>
        {pattern.canCurate && !gateway &&
          actions.map(x => (
            <Button
              key={x.status}
              size="sm"
              variant={x.label === 'Reject' ? 'danger' : x.label === 'Approve' ? 'primary' : 'secondary'}
              type="button"
              disabled={isBusy}
              onClick={() => onReview(pattern, x.status)}
            >
              {x.label}
            </Button>
          ))}
      </ModalFooter>
    </Modal>
  );
}

interface PatchDetailModalProps {
  patch: DocumentationPatchEntry | null;
  onClose: () => void;
  onApply: (patch: DocumentationPatchEntry) => void;
  onReject: (patch: DocumentationPatchEntry) => void;
  isBusy: boolean;
}

/** A documentation patch in full — current and proposed content side by side — with who applied or rejected it. */
export function PatchDetailModal({ patch, onClose, onApply, onReject, isBusy }: PatchDetailModalProps) {
  if (patch === null) return null;

  const isProposed = patch.status === McpDocPatchStatus.Proposed;
  const decision = patch.status === McpDocPatchStatus.Rejected ? 'Rejected by' : 'Applied by';

  return (
    <Modal open onClose={onClose} width={900} ariaLabel="Documentation patch">
      <ModalHeader
        eyebrow="Documentation patch"
        emphasis={PATCH_TARGET_LABEL[patch.targetType] ?? String(patch.targetType)}
        suffix={<span className="mono text-base font-normal">{patch.targetIdentifier}</span>}
        onClose={onClose}
      />
      <ModalBody className="flex flex-col gap-4">
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-3">
          <Fact label="Status">
            <Pill tone="info">{PATCH_STATUS_LABEL[patch.status] ?? patch.status}</Pill>
          </Fact>
          <Fact label="Supporting signals">{patch.supportingSignalCount}</Fact>
          {!isProposed && <Fact label={decision}>{decidedBy(patch.appliedByName, patch.appliedAt)}</Fact>}
        </div>
        <div className="grid gap-3 md:grid-cols-2">
          <ContentBlock label="Current" text={patch.currentContent} />
          <ContentBlock label="Proposed" text={patch.proposedContent} />
        </div>
        <ContentBlock label="Reasoning" text={patch.reasoning} />
        {isProposed && !patch.canCurate && (
          <Banner tone="info" title="You cannot apply or reject this patch" sub={patchCurateReason(patch.targetType)} />
        )}
      </ModalBody>
      <ModalFooter hints={closeHint}>
        {isProposed && patch.canCurate && (
          <>
            <Button variant="danger" size="sm" type="button" disabled={isBusy} onClick={() => onReject(patch)}>
              Reject
            </Button>
            <Button variant="primary" size="sm" type="button" disabled={isBusy} onClick={() => onApply(patch)}>
              Apply
            </Button>
          </>
        )}
      </ModalFooter>
    </Modal>
  );
}
