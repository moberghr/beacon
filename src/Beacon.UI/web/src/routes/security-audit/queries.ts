import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useSearchParams } from 'react-router-dom';
import { beaconApi } from '@/api/client';
import { unwrap } from '@/lib/api';
import { createSimpleMutation } from '@/lib/mutations';
import { usePagedList } from '@/lib/usePagedList';
import type { ListParams } from '@/lib/paging';
import { actionsOf } from './catalogue';

/** One event of the security audit trail as the server returns it (times as ISO strings). */
export interface SecurityAuditEvent {
  id: number;
  eventGuid: string;
  createdTime: string;
  category: string;
  action: string;
  outcome: string;
  actorKind: string;
  actorUserId: number | null;
  apiKeyId: number | null;
  callerHash: string | null;
  projectId: number | null;
  resourceType: string | null;
  resourceId: string | null;
  /** Identifiers, counts, flags and codes only; a value that is not a code is stored as `[withheld]`. */
  detailsJson: string | null;
  traceId: string | null;
  /** The integrity key the event's signature was made with. */
  keyId: string;
}

export interface VerifySecurityAuditBody {
  from: Date;
  to: Date;
  /** Only these events of the window; all of them when omitted. */
  eventIds?: number[];
}

/**
 * What the server says about one stored event: its signature matches (`intact`), its fields do not match it
 * (`modified`), it is signed with a key this deployment does not hold (`unknown_key`), or no such event is in the window
 * and the range a genuine event can be dated in (`not_found`). Only `intact` says an event verified.
 */
export type SecurityAuditVerdictCode = 'intact' | 'modified' | 'unknown_key' | 'not_found';

export interface SecurityAuditEventVerdict {
  eventId: number;
  verdict: SecurityAuditVerdictCode | string;
}

export interface VerifySecurityAuditResult {
  checkedCount: number;
  intactCount: number;
  modifiedCount: number;
  unknownKeyCount: number;
  /** Events dated outside the range a genuine event can have: counted as failing, not checked. */
  outOfRangeCount: number;
  /** `success` (every checked event intact), `failure` (any failed or out of range) or `empty` (nothing to check). */
  outcome: 'success' | 'failure' | 'empty' | string;
  /** The first failing events (at most 100; the counts say how many failed). */
  failedEventIds: number[];
  /** For a verification by id: one verdict per requested id. Null for a whole window. */
  verdicts: SecurityAuditEventVerdict[] | null;
}

/** The longest window the server reads or verifies at once. */
export const MAX_WINDOW_DAYS = 93;

const DAY_MS = 24 * 60 * 60 * 1000;
const DATE_PATTERN = /^\d{4}-\d{2}-\d{2}$/;

/** The UTC calendar day of `value`, as a date input shows it. */
export function utcDay(value: Date): string {
  return value.toISOString().slice(0, 10);
}

/** The last seven UTC days, today included. */
export function defaultWindow(now: Date = new Date()): { from: string; to: string } {
  return { from: utcDay(new Date(now.getTime() - 6 * DAY_MS)), to: utcDay(now) };
}

export type AuditWindow = { from: Date; to: Date; error?: undefined } | { error: string; from?: undefined; to?: undefined };

/**
 * The window from the start of UTC day `from` to the end of UTC day `to`, or why it cannot be read: a missing or malformed
 * day, an end before the start, or more than {@link MAX_WINDOW_DAYS} days (the server refuses those too).
 */
export function windowOf(from: string, to: string): AuditWindow {
  if (!DATE_PATTERN.test(from) || !DATE_PATTERN.test(to)) {
    return { error: 'Choose the first and the last day of the window.' };
  }

  const start = new Date(`${from}T00:00:00.000Z`);
  const end = new Date(`${to}T23:59:59.999Z`);
  if (Number.isNaN(start.getTime()) || Number.isNaN(end.getTime())) {
    return { error: 'Choose the first and the last day of the window.' };
  }

  if (end < start) {
    return { error: 'The last day must not be before the first.' };
  }

  if (end.getTime() - start.getTime() > MAX_WINDOW_DAYS * DAY_MS) {
    return { error: `The window must not exceed ${MAX_WINDOW_DAYS} days.` };
  }

  return { from: start, to: end };
}

export interface SecurityAuditFilters extends Record<string, string> {
  from: string;
  to: string;
  category: string;
  action: string;
  actorUserId: string;
  resourceType: string;
  resourceId: string;
  projectId: string;
}

const KEY = ['security-audit'] as const;

/** A positive whole number typed into an id filter; anything else filters nothing. */
function idParam(value: string): number | undefined {
  return /^\d+$/.test(value.trim()) && Number(value) > 0 ? Number(value) : undefined;
}

/**
 * The API parameters for the page's filters: the window as UTC times, ids as numbers, and the action only while it is one
 * of the chosen category's (another would match nothing).
 */
export function auditListParams(filters: SecurityAuditFilters): ListParams {
  const auditWindow = windowOf(filters.from, filters.to);

  return {
    from: auditWindow.from?.toISOString(),
    to: auditWindow.to?.toISOString(),
    category: filters.category,
    action: actionsOf(filters.category).includes(filters.action) ? filters.action : '',
    actorUserId: idParam(filters.actorUserId),
    resourceType: filters.resourceType,
    resourceId: filters.resourceId.trim(),
    projectId: idParam(filters.projectId),
  };
}

/**
 * The trail, newest first, filtered and paged in the URL. Nothing is read while the window cannot be (see
 * {@link windowOf}); the server records every read.
 */
export function useSecurityAuditList(defaults: { from: string; to: string }) {
  // The window is read from the URL as usePagedList reads it, to hold the request back while the window is invalid.
  const [searchParams] = useSearchParams();
  const auditWindow = windowOf(searchParams.get('from') ?? defaults.from, searchParams.get('to') ?? defaults.to);
  const list = usePagedList<SecurityAuditEvent, SecurityAuditFilters>({
    queryKey: KEY,
    path: '/beacon/api/security-audit',
    pageSize: 50,
    defaultSort: { column: 'createdTime', direction: 'desc' },
    filters: {
      from: defaults.from,
      to: defaults.to,
      category: '',
      action: '',
      actorUserId: '',
      resourceType: '',
      resourceId: '',
      projectId: '',
    },
    mapFilters: auditListParams,
    enabled: auditWindow.error === undefined,
  });

  return { ...list, auditWindow };
}

/**
 * Verifies the events of a window (or the given ones of it) against their integrity signatures; the server records it.
 * A failure is not toasted: the page shows it, a verification already running (429) apart from any other.
 */
export function useVerifySecurityAudit() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<VerifySecurityAuditBody, VerifySecurityAuditResult>({
      qc,
      mutationFn: async (body) =>
        unwrap<VerifySecurityAuditResult>(
          await beaconApi().verifySecurityAudit({ from: body.from, to: body.to, eventIds: body.eventIds ?? null }),
        ),
      errorFallback: 'The verification failed',
      errorToast: false,
    }),
  );
}
