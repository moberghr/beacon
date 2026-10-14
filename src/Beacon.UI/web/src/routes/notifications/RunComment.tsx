/**
 * A run's recorded comment, readable: the server records one reason for the run (such as "Skipped: every recipient waits
 * for approval of where it delivers, so nobody was notified. …"), or one reason per recipient in the form
 * `Recipient 7: <reason> Recipient 9: <reason>` (a failed delivery, or a recipient held because where it delivers waits
 * for approval). Per-recipient reasons are grouped by reason, each with the recipients it names, by name where the page
 * knows them.
 */

/** One reason of a run's comment, and the recipients it names (none: the reason is the run's). */
export interface RunCommentPart {
  reason: string;
  recipientIds: number[];
}

const PER_RECIPIENT = /(?:^| )Recipient (\d*): /g;

/** Splits a recorded comment into its reasons, grouping the per-recipient ones by reason in the order they appear. */
export function parseRunComment(comment: string | null | undefined): RunCommentPart[] {
  const text = comment?.trim();
  if (!text) {
    return [];
  }

  const markers = [...text.matchAll(PER_RECIPIENT)];
  if (markers.length === 0 || markers[0].index !== 0) {
    return [{ reason: text, recipientIds: [] }];
  }

  const byReason = new Map<string, number[]>();
  markers.forEach((marker, i) => {
    const start = (marker.index ?? 0) + marker[0].length;
    const end = i + 1 < markers.length ? markers[i + 1].index ?? text.length : text.length;
    const reason = text.slice(start, end).trim();
    const id = Number.parseInt(marker[1], 10);
    const ids = byReason.get(reason) ?? [];
    if (Number.isFinite(id)) {
      ids.push(id);
    }
    byReason.set(reason, ids);
  });

  return [...byReason.entries()].map(([reason, recipientIds]) => ({ reason, recipientIds }));
}

/** A recipient by name when the page knows it, else by id. */
function recipientLabel(id: number, names?: ReadonlyMap<number, string>): string {
  return names?.get(id) ?? `Recipient #${id}`;
}

/** The recorded comment of a run, one line per reason, the recipients a per-recipient reason names first. */
export function RunComment({
  comment,
  recipientNames,
  className,
}: {
  comment: string | null | undefined;
  /** The names of the subscription's recipients by id, to name the recipients a reason is about. */
  recipientNames?: ReadonlyMap<number, string>;
  className?: string;
}) {
  const parts = parseRunComment(comment);
  if (parts.length === 0) {
    return null;
  }

  return (
    <ul className={`m-0 p-0 list-none flex flex-col gap-0.5 text-xs text-text-muted ${className ?? ''}`} aria-label="Run notes">
      {parts.map(x => (
        <li key={x.reason}>
          {x.recipientIds.length > 0 && (
            <span className="font-medium text-text">
              {x.recipientIds.map(y => recipientLabel(y, recipientNames)).join(', ')}:{' '}
            </span>
          )}
          {x.reason}
        </li>
      ))}
    </ul>
  );
}
