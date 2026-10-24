import { useMemo, useState, type ReactNode } from 'react';
import { AlertTriangle, CheckCircle2, Eye, Hourglass, RotateCcw, ScrollText, ShieldCheck } from 'lucide-react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { Dialog } from '@/components/ui/Dialog';
import {
  Banner,
  Button,
  Card,
  CardActions,
  CardBody,
  CardHead,
  CardSub,
  CardTitle,
  Field,
  Input,
  PageHeader,
  Pill,
  Select,
} from '@/components/beacon';
import { useRequirePermission } from '@/auth/useRequirePermission';
import { AuditExportButton } from './AuditExportButton';
import { describeError, httpErrorInfo } from '@/lib/api';
import { formatDateTime, formatNumber } from '@/lib/format';
import { useSearchFilter } from '@/lib/usePagedList';
import {
  SECURITY_AUDIT_ACTOR_KINDS,
  SECURITY_AUDIT_CATEGORIES,
  SECURITY_AUDIT_RESOURCE_TYPES,
  WITHHELD_VALUE,
  actionsOf,
  describeAction,
} from './catalogue';
import {
  MAX_WINDOW_DAYS,
  defaultWindow,
  useSecurityAuditList,
  useVerifySecurityAudit,
  type SecurityAuditEvent,
  type VerifySecurityAuditResult,
} from './queries';

const GRID_TEMPLATE = '1.1fr 1.9fr 0.7fr 1.3fr 1.2fr 0.6fr 0.8fr 150px';

/**
 * What a verification found for one event, as the server judged it: its signature matches (`intact`), its fields do not
 * (`modified`), it is signed with a key this deployment does not hold (`unknown_key`), or it is not in the window
 * (`not_found`). `unverified`: the answer named no verdict for it, so nothing is claimed. Only `intact` is verified.
 */
export type Verdict = 'intact' | 'modified' | 'unknown_key' | 'not_found' | 'unverified';

const SERVER_VERDICTS: readonly Verdict[] = ['intact', 'modified', 'unknown_key', 'not_found'];

/** Why a verification did not run: another one was running (429), or it failed. */
type VerifyFailure = { busy: boolean; message: string };

/** For an Auditor (an Admin holds the permission); the server refuses everyone else. */
export default function SecurityAuditPage() {
  const allowed = useRequirePermission('Auditor');

  if (allowed === undefined) {
    return (
      <div className="flex flex-col gap-3 p-7">
        <PageHeader eyebrow="Audit" prefix="Security" emphasis="audit" sub={<span className="text-text-muted">Loading…</span>} />
      </div>
    );
  }

  if (allowed === false) {
    return null;
  }

  return <SecurityAuditView />;
}

function SecurityAuditView() {
  const defaults = useMemo(() => defaultWindow(), []);
  const list = useSecurityAuditList(defaults);
  const { filters, setFilter, resetFilters, auditWindow } = list;
  const verify = useVerifySecurityAudit();
  const [verdicts, setVerdicts] = useState<Record<number, Verdict>>({});
  const [summary, setSummary] = useState<{ requested: number; result: VerifySecurityAuditResult } | null>(null);
  const [verifyFailure, setVerifyFailure] = useState<VerifyFailure | null>(null);
  const [shown, setShown] = useState<SecurityAuditEvent | null>(null);
  const [actorInput, setActorInput] = useSearchFilter(filters.actorUserId, value => setFilter('actorUserId', value));
  const [resourceIdInput, setResourceIdInput] = useSearchFilter(filters.resourceId, value => setFilter('resourceId', value));
  const [projectInput, setProjectInput] = useSearchFilter(filters.projectId, value => setFilter('projectId', value));
  // An action of another category than the chosen one is not applied (the server would match nothing).
  const categoryActions = actionsOf(filters.category);
  const action = categoryActions.includes(filters.action) ? filters.action : '';

  const runVerification = async (events: SecurityAuditEvent[]) => {
    if (auditWindow.error !== undefined || events.length === 0) return;
    const eventIds = events.map(x => x.id);
    setVerifyFailure(null);
    try {
      const result = await verify.mutateAsync({ from: auditWindow.from, to: auditWindow.to, eventIds });
      setVerdicts(previous => ({ ...previous, ...verdictsOf(eventIds, result) }));
      setSummary({ requested: eventIds.length, result });
    } catch (err) {
      // Shown on the page (the mutation does not toast): a verification already running is no failure of the trail.
      setVerifyFailure({ busy: httpErrorInfo(err)?.status === 429, message: describeError(err, 'The verification failed') });
    }
  };

  const columns: Column<SecurityAuditEvent>[] = [
    {
      key: 'time',
      sortKey: 'createdTime',
      header: 'Time',
      render: x => <span className="text-text-muted">{formatDateTime(x.createdTime)}</span>,
    },
    {
      key: 'action',
      header: 'Action',
      render: x => (
        <div className="flex flex-col min-w-0">
          <span className="text-text truncate">{describeAction(x.action)}</span>
          <span className="mono text-2xs text-text-subtle truncate">{x.action}</span>
        </div>
      ),
    },
    { key: 'outcome', header: 'Outcome', render: x => <OutcomePill outcome={x.outcome} /> },
    {
      key: 'actor',
      header: 'Actor',
      render: x => x.actorUserId != null
        ? (
          <button
            type="button"
            className="text-left text-text hover:underline"
            title="Show only this actor's events"
            onClick={() => setFilter('actorUserId', String(x.actorUserId))}
          >
            {actorOf(x)}
          </button>
        )
        : <span className="text-text">{actorOf(x)}</span>,
    },
    {
      key: 'resource',
      header: 'Resource',
      render: x => x.resourceType
        ? <span className="mono text-xs">{x.resourceType}{x.resourceId ? ` #${x.resourceId}` : ''}</span>
        : <span className="text-text-muted">—</span>,
    },
    {
      key: 'project',
      header: 'Project',
      render: x => x.projectId != null ? <span className="mono text-xs">#{x.projectId}</span> : <span className="text-text-muted">—</span>,
    },
    { key: 'integrity', header: 'Integrity', render: x => <VerdictPill verdict={verdicts[x.id]} /> },
    {
      key: 'actions',
      header: '',
      render: x => (
        <div className="flex gap-1.5 justify-end">
          <Button type="button" size="sm" variant="ghost" icon={<Eye />} onClick={() => setShown(x)} aria-label={`Details of event ${x.id}`}>
            Details
          </Button>
          <Button
            type="button"
            size="sm"
            onClick={() => runVerification([x])}
            disabled={verify.isPending}
            aria-label={`Verify event ${x.id}`}
          >
            Verify
          </Button>
        </div>
      ),
    },
  ];

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        eyebrow="Audit"
        prefix="Security"
        emphasis="audit"
        sub="Sign-ins, identities, keys, access, data sources, delivery, settings, approvals and curation, as recorded. Reading and verifying the trail are recorded too."
      />

      <Card>
        <CardBody>
          <div className="grid gap-3 grid-cols-2 md:grid-cols-4">
            <Field label="From (UTC)" htmlFor="audit-from">
              <Input id="audit-from" type="date" value={filters.from} onChange={e => setFilter('from', e.target.value)} />
            </Field>
            <Field label="To (UTC)" htmlFor="audit-to">
              <Input id="audit-to" type="date" value={filters.to} onChange={e => setFilter('to', e.target.value)} />
            </Field>
            <Field label="Category" htmlFor="audit-category">
              <Select id="audit-category" value={filters.category} onChange={e => setFilter('category', e.target.value)}>
                <option value="">All categories</option>
                {SECURITY_AUDIT_CATEGORIES.map(x => <option key={x.code} value={x.code}>{x.label}</option>)}
              </Select>
            </Field>
            <Field label="Action" htmlFor="audit-action">
              <Select id="audit-action" value={action} onChange={e => setFilter('action', e.target.value)}>
                <option value="">All actions</option>
                {categoryActions.map(x => <option key={x} value={x}>{x}</option>)}
              </Select>
            </Field>
            <Field label="Actor user id" htmlFor="audit-actor">
              <Input
                id="audit-actor"
                type="number"
                min={1}
                inputMode="numeric"
                placeholder="any"
                value={actorInput}
                onChange={e => setActorInput(e.target.value)}
              />
            </Field>
            <Field label="Resource type" htmlFor="audit-resource-type">
              <Select id="audit-resource-type" value={filters.resourceType} onChange={e => setFilter('resourceType', e.target.value)}>
                <option value="">Any resource</option>
                {SECURITY_AUDIT_RESOURCE_TYPES.map(x => <option key={x} value={x}>{x}</option>)}
              </Select>
            </Field>
            <Field label="Resource id" htmlFor="audit-resource-id">
              <Input
                id="audit-resource-id"
                placeholder="any"
                value={resourceIdInput}
                onChange={e => setResourceIdInput(e.target.value)}
              />
            </Field>
            <Field label="Project id" htmlFor="audit-project">
              <Input
                id="audit-project"
                type="number"
                min={1}
                inputMode="numeric"
                placeholder="any"
                value={projectInput}
                onChange={e => setProjectInput(e.target.value)}
              />
            </Field>
          </div>
          <div className="flex items-center justify-between gap-3 mt-3">
            <span className="text-xs text-text-muted">
              The window runs over whole UTC days, at most {MAX_WINDOW_DAYS} at a time; times are shown in your time zone.
            </span>
            <Button type="button" size="sm" variant="ghost" icon={<RotateCcw />} onClick={resetFilters}>
              Reset filters
            </Button>
          </div>
        </CardBody>
      </Card>

      {auditWindow.error !== undefined && (
        <Banner tone="warn" icon={<AlertTriangle />} title="The window cannot be read" sub={auditWindow.error} />
      )}

      {verifyFailure && (
        verifyFailure.busy
          ? <Banner tone="warn" icon={<Hourglass />} title="Verification busy" sub={verifyFailure.message} />
          : <Banner tone="crit" icon={<AlertTriangle />} title="The verification did not run" sub={verifyFailure.message} />
      )}

      {summary && <VerificationBanner requested={summary.requested} result={summary.result} />}

      <Card>
        <CardHead>
          <CardTitle>Events</CardTitle>
          <CardSub>{formatNumber(list.data?.totalCount ?? 0)} in the window</CardSub>
          <CardActions>
            <AuditExportButton
              fileName={`security-audit-${filters.from}_${filters.to}.json`}
              fetchPage={list.fetchExportPage}
              firstPage={0}
              confirmMessage="Downloads every event of the window as filtered (up to 5000) as an unencrypted JSON file. Each page read is recorded in the audit."
              disabled={auditWindow.error !== undefined || (list.data?.totalCount ?? 0) === 0}
            />
            <Button
              type="button"
              size="sm"
              icon={<ShieldCheck />}
              onClick={() => runVerification(list.items)}
              disabled={verify.isPending || list.items.length === 0 || auditWindow.error !== undefined}
            >
              {verify.isPending ? 'Verifying…' : 'Verify shown events'}
            </Button>
          </CardActions>
        </CardHead>
        {list.isError
          ? (
            <EmptyState
              icon={<AlertTriangle />}
              title="The security audit could not be read"
              description={list.error instanceof Error ? list.error.message : 'Unknown error'}
            />
          )
          : (
            <DataTable
              ariaLabel="Security audit events"
              columns={columns}
              rows={list.items}
              {...list.tableProps}
              rowKey={x => x.id}
              gridTemplate={GRID_TEMPLATE}
              empty={
                <EmptyState
                  icon={<ScrollText />}
                  title={list.isLoading ? 'Loading events…' : 'No events'}
                  description={list.isLoading || auditWindow.error !== undefined ? '' : 'Nothing was recorded in this window with these filters.'}
                />
              }
            />
          )}
      </Card>

      <EventDetailsDialog
        event={shown}
        verdict={shown ? verdicts[shown.id] : undefined}
        verifying={verify.isPending}
        onVerify={event => runVerification([event])}
        onClose={() => setShown(null)}
      />
    </div>
  );
}

/**
 * Each requested event's verdict, as the server gave it per id. An id the answer gives no verdict for (or a verdict this
 * page does not know) is `unverified`: an event is shown intact only when the server said so.
 */
export function verdictsOf(eventIds: number[], result: VerifySecurityAuditResult): Record<number, Verdict> {
  const given = new Map((result.verdicts ?? []).map(x => [x.eventId, x.verdict]));
  const verdicts: Record<number, Verdict> = {};
  for (const id of eventIds) {
    const verdict = given.get(id) as Verdict | undefined;
    verdicts[id] = verdict !== undefined && SERVER_VERDICTS.includes(verdict) ? verdict : 'unverified';
  }

  return verdicts;
}

function VerificationBanner({ requested, result }: { requested: number; result: VerifySecurityAuditResult }) {
  const noun = (count: number) => (count === 1 ? 'event' : 'events');
  const deletions = 'A deleted event leaves nothing to check: verification finds changed and forged events, not missing ones.';
  const notFound = (result.verdicts ?? []).filter(x => x.verdict === 'not_found').length;
  const notFoundNote = notFound > 0
    ? ` ${formatNumber(notFound)} not found in the window (never stored, deleted, or dated where no genuine event can be).`
    : '';

  if (result.outcome === 'empty') {
    return (
      <Banner
        tone="warn"
        icon={<AlertTriangle />}
        title="Nothing was verified"
        sub={`None of the ${formatNumber(requested)} ${noun(requested)} was found in the window to check: this is not a success.${notFoundNote} ${deletions}`}
      />
    );
  }

  const failedCount = result.modifiedCount + result.unknownKeyCount + result.outOfRangeCount;
  if (failedCount === 0) {
    return (
      <Banner
        tone="ok"
        icon={<CheckCircle2 />}
        title={`${formatNumber(result.intactCount)} of ${formatNumber(requested)} ${noun(requested)} verified intact`}
        sub={`Each verified event's signature matches it.${notFoundNote} ${deletions}`}
      />
    );
  }

  const listed = result.failedEventIds.length < failedCount ? `The first ${formatNumber(result.failedEventIds.length)} failing` : 'Failing';
  return (
    <Banner
      tone="crit"
      icon={<AlertTriangle />}
      title={`${formatNumber(failedCount)} of ${formatNumber(result.checkedCount + result.outOfRangeCount)} ${noun(result.checkedCount + result.outOfRangeCount)} failed verification`}
      sub={
        `${formatNumber(result.modifiedCount)} changed or forged; ${formatNumber(result.unknownKeyCount)} signed with an ` +
        `integrity key this deployment does not hold; ${formatNumber(result.outOfRangeCount)} dated outside the range a ` +
        `genuine event can have. ${listed}: ${result.failedEventIds.map(x => `#${x}`).join(', ')}.${notFoundNote} ${deletions}`
      }
    />
  );
}

function OutcomePill({ outcome }: { outcome: string }) {
  const tone = outcome === 'success' ? 'ok' : outcome === 'refused' ? 'warn' : outcome === 'failure' ? 'crit' : 'neutral';
  return <Pill tone={tone}>{outcome}</Pill>;
}

function VerdictPill({ verdict }: { verdict: Verdict | undefined }) {
  if (verdict === 'intact') return <Pill tone="ok">Intact</Pill>;
  if (verdict === 'modified') return <Pill tone="crit" title="Its fields do not match its signature: changed or forged">Modified</Pill>;
  if (verdict === 'unknown_key') {
    return <Pill tone="warn" title="Signed with an integrity key this deployment does not hold: it cannot be checked">Unknown key</Pill>;
  }
  if (verdict === 'not_found') return <Pill tone="warn" title="Not in the window verified">Not found</Pill>;
  if (verdict === 'unverified') return <Pill tone="neutral" title="The verification gave no verdict for this event">Unverified</Pill>;
  return <span className="text-xs text-text-muted">Not verified</span>;
}

/** Who acted, as far as the event names them: a user by id, an API key by id, else a keyed caller hash or the kind. */
export function actorOf(event: SecurityAuditEvent): string {
  const kind = SECURITY_AUDIT_ACTOR_KINDS[event.actorKind] ?? event.actorKind;
  const parts = [kind];
  if (event.actorUserId != null) parts.push(`user #${event.actorUserId}`);
  if (event.apiKeyId != null) parts.push(`key #${event.apiKeyId}`);
  if (event.actorUserId == null && event.callerHash) parts.push(`${event.callerHash.slice(0, 8)}…`);
  return parts.join(' · ');
}

type DetailValue = string | number | boolean | null | DetailValue[] | { [key: string]: DetailValue };

/** The event's details as key and value; null when there are none, `undefined` when they cannot be read. */
export function detailsOf(json: string | null): [string, DetailValue][] | null | undefined {
  if (!json) return null;
  try {
    const parsed = JSON.parse(json) as unknown;
    return parsed !== null && typeof parsed === 'object' && !Array.isArray(parsed)
      ? Object.entries(parsed as Record<string, DetailValue>)
      : undefined;
  } catch {
    return undefined;
  }
}

function DetailValueView({ value }: { value: DetailValue }) {
  if (value === WITHHELD_VALUE) {
    return <Pill tone="warn" title="Not a code: the server stored it withheld">withheld</Pill>;
  }
  if (value === null) return <span className="text-text-muted">—</span>;
  if (typeof value === 'boolean') return <span>{value ? 'yes' : 'no'}</span>;
  if (Array.isArray(value)) {
    if (value.length === 0) return <span className="text-text-muted">none</span>;
    return (
      <span className="flex flex-wrap gap-1">
        {value.map((x, i) => <DetailValueView key={i} value={x} />)}
      </span>
    );
  }
  if (typeof value === 'object') return <span className="mono text-xs">{JSON.stringify(value)}</span>;
  return <span className="mono text-xs break-all">{String(value)}</span>;
}

function EventDetailsDialog({
  event,
  verdict,
  verifying,
  onVerify,
  onClose,
}: {
  event: SecurityAuditEvent | null;
  verdict: Verdict | undefined;
  verifying: boolean;
  onVerify: (event: SecurityAuditEvent) => void;
  onClose: () => void;
}) {
  if (event == null) return null;
  const details = detailsOf(event.detailsJson);
  const rows: [string, ReactNode][] = [
    ['Event', <span className="mono text-xs">#{event.id} · {event.eventGuid}</span>],
    ['Time', <span>{formatDateTime(event.createdTime)} <span className="mono text-2xs text-text-subtle">{event.createdTime}</span></span>],
    ['Action', <span className="mono text-xs">{event.action}</span>],
    ['Category', <span className="mono text-xs">{event.category}</span>],
    ['Outcome', <OutcomePill outcome={event.outcome} />],
    ['Actor', <span>{actorOf(event)}</span>],
    ['Caller hash', event.callerHash ? <span className="mono text-xs break-all">{event.callerHash}</span> : <span className="text-text-muted">—</span>],
    ['Resource', event.resourceType ? <span className="mono text-xs">{event.resourceType}{event.resourceId ? ` #${event.resourceId}` : ''}</span> : <span className="text-text-muted">—</span>],
    ['Project', event.projectId != null ? <span className="mono text-xs">#{event.projectId}</span> : <span className="text-text-muted">—</span>],
    ['Trace id', event.traceId ? <span className="mono text-xs break-all">{event.traceId}</span> : <span className="text-text-muted">—</span>],
    ['Integrity key', <span className="mono text-xs">{event.keyId}</span>],
    ['Integrity', <VerdictPill verdict={verdict} />],
  ];

  return (
    <Dialog
      open
      onClose={onClose}
      title={describeAction(event.action)}
      sub={`Event #${event.id}`}
      size="lg"
      footer={
        <>
          <Button type="button" icon={<ShieldCheck />} onClick={() => onVerify(event)} disabled={verifying}>
            {verifying ? 'Verifying…' : 'Verify this event'}
          </Button>
          <Button type="button" variant="primary" onClick={onClose}>Close</Button>
        </>
      }
    >
      <dl className="m-0 grid gap-x-4 gap-y-2 text-sm" style={{ gridTemplateColumns: '140px 1fr' }}>
        {rows.map(([label, value]) => (
          <div key={label} className="contents">
            <dt className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted pt-0.5">{label}</dt>
            <dd className="m-0 min-w-0">{value}</dd>
          </div>
        ))}
      </dl>

      <h3 className="m-0 mt-5 mb-2 text-sm font-semibold text-text">Details</h3>
      {details === null && <p className="m-0 text-sm text-text-muted">This event has no details.</p>}
      {details === undefined && <p className="m-0 text-sm text-crit">The details could not be read.</p>}
      {details && (
        <dl className="m-0 grid gap-x-4 gap-y-1.5 text-sm" style={{ gridTemplateColumns: '180px 1fr' }}>
          {details.map(([key, value]) => (
            <div key={key} className="contents">
              <dt className="mono text-xs text-text-muted pt-0.5">{key}</dt>
              <dd className="m-0 min-w-0"><DetailValueView value={value} /></dd>
            </div>
          ))}
        </dl>
      )}
      <p className="m-0 mt-4 text-xs text-text-muted">
        The trail keeps identifiers, counts, flags and short codes only. Names, e-mail addresses, typed sign-in names,
        free text, SQL, IP addresses and secrets are never stored: a value that is not a code is stored as withheld.
      </p>
    </Dialog>
  );
}
