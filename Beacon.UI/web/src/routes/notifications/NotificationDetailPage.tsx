import { useMemo } from 'react';
import { Link, useParams } from 'react-router-dom';
import { AlertTriangle, Bell } from 'lucide-react';
import { Card, CardBody, PageHeader, Pill, type PillProps } from '@/components/beacon';
import { EmptyState } from '@/components/data/EmptyState';
import { formatDateTime, formatNumber } from '@/lib/format';
import { useNotificationDetailQuery } from './queries';

const STATUS_LABELS: Record<number, { label: string; tone: PillProps['tone'] }> = {
  1: { label: 'Created', tone: 'neutral' },
  2: { label: 'Sent', tone: 'ok' },
  3: { label: 'Silenced', tone: 'neutral' },
  4: { label: 'No results', tone: 'warn' },
  5: { label: 'Timeout', tone: 'crit' },
  6: { label: 'Below threshold', tone: 'neutral' },
  7: { label: 'Failed', tone: 'crit' },
};

const TYPE_LABELS: Record<number, string> = {
  1: 'Teams',
  2: 'Email',
  3: 'Jira',
  4: 'Slack',
  5: 'Webhook',
};

interface ParsedResults {
  rows: Array<Record<string, unknown>>;
  columns: string[];
}

function tryParseResults(raw: string | null): ParsedResults | null {
  if (raw === null || raw.trim().length === 0) {
    return null;
  }
  try {
    const json = JSON.parse(raw);
    if (Array.isArray(json) && json.length > 0 && typeof json[0] === 'object' && json[0] !== null) {
      const rows = json as Array<Record<string, unknown>>;
      const columns = Object.keys(rows[0]);
      return { rows, columns };
    }
    return null;
  } catch {
    return null;
  }
}

export default function NotificationDetailPage() {
  const { id } = useParams();
  const notificationId = id ? Number.parseInt(id, 10) : Number.NaN;
  const { data, isLoading, isError, error } = useNotificationDetailQuery(notificationId);

  const entry = data?.entry ?? null;

  const parsed = useMemo(() => entry ? tryParseResults(entry.results) : null, [entry]);

  if (Number.isNaN(notificationId)) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <PageHeader eyebrow="Activity" emphasis="Notification" />
        <EmptyState icon={<AlertTriangle />} title="Invalid notification id" description={String(id)} />
      </div>
    );
  }

  if (isLoading) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <PageHeader eyebrow="Activity" emphasis="Notification" sub={<span className="text-text-muted">Loading…</span>} />
      </div>
    );
  }

  if (isError) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <PageHeader eyebrow="Activity" emphasis="Notification" />
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load notification"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      </div>
    );
  }

  if (entry === null) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <PageHeader
          eyebrow="Activity"
          emphasis="Notification"
          sub={<Link to="/notifications" className="text-text-muted">← back to notifications</Link>}
        />
        <EmptyState icon={<Bell />} title="Notification not found" />
      </div>
    );
  }

  const status = STATUS_LABELS[entry.status] ?? { label: String(entry.status), tone: 'neutral' as const };
  const typeLabel = TYPE_LABELS[entry.type] ?? String(entry.type);

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        eyebrow="Activity"
        emphasis={`Notification #${entry.id}`}
        sub={
          <span className="text-text-muted">
            <Link to="/notifications" className="text-text-muted">Notifications</Link>
            <span className="mx-1.5">/</span>
            #{entry.id}
          </span>
        }
        actions={<Pill tone={status.tone}>{status.label}</Pill>}
      />

      <Card className="min-w-0 overflow-hidden">
        <CardBody className="min-w-0">
          <div className="grid grid-cols-1 md:grid-cols-3 gap-4 [&>*]:min-w-0">
            <Section title="Notification">
              <FieldRow label="Type" value={typeLabel} />
              <FieldRow label="Created" value={formatDateTime(entry.createdTime)} />
              <FieldRow label="Sent at" value={formatDateTime(entry.sentAt)} />
            </Section>

            <Section title="Recipient">
              <FieldRow label="Name" value={entry.recipientName} />
            </Section>

            <Section title="Query">
              <FieldRow label="Name" value={
                <Link className="text-text-muted" to={`/queries/${entry.queryId}`}>{entry.queryName}</Link>
              } />
              <FieldRow label="Subscription" value={
                <Link className="text-text-muted" to={`/subscriptions/${entry.subscriptionId}`}>
                  #{entry.subscriptionId}
                </Link>
              } />
              <FieldRow label="Execution time" value={`${entry.executionTimeMs.toFixed(2)} ms`} />
              <FieldRow label="Result count" value={entry.resultCount === null ? '—' : formatNumber(entry.resultCount)} />
            </Section>
          </div>
        </CardBody>
      </Card>

      <ResultsCard rawResults={entry.results} parsed={parsed} />
    </div>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div>
      <h3 className="m-0 mb-2 text-sm font-semibold text-text">{title}</h3>
      <div className="flex flex-col gap-1.5">
        {children}
      </div>
    </div>
  );
}

function FieldRow({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div className="text-sm">
      <span className="text-text-muted mr-1.5">{label}:</span>
      <span className="text-text">{value}</span>
    </div>
  );
}

function ResultsCard({ rawResults, parsed }: { rawResults: string | null; parsed: ParsedResults | null }) {
  if (rawResults === null || rawResults.trim().length === 0) {
    return (
      <Card>
        <CardBody>
          <span className="text-text-muted">No results were stored for this notification.</span>
        </CardBody>
      </Card>
    );
  }

  if (parsed === null) {
    return (
      <Card>
        <CardBody>
          <h3 className="m-0 mb-2 text-sm font-semibold text-text">Stored results</h3>
          <pre className="mono text-xs p-3 bg-surface-2 rounded-sm max-h-[400px] overflow-auto m-0 whitespace-pre-wrap break-all">
            {rawResults}
          </pre>
        </CardBody>
      </Card>
    );
  }

  const truncated = parsed.rows.length > 100;
  const rows = truncated ? parsed.rows.slice(0, 100) : parsed.rows;

  return (
    <Card className="min-w-0 overflow-hidden">
      <CardBody className="min-w-0">
        <h3 className="m-0 mb-2 text-sm font-semibold text-text">
          Stored results <span className="text-text-muted font-normal text-xs">
            ({formatNumber(parsed.rows.length)} rows{truncated ? ' — showing first 100' : ''})
          </span>
        </h3>
        <div className="overflow-auto max-h-[480px] max-w-full">
          <table className="border-collapse text-xs">
            <thead>
              <tr>
                {parsed.columns.map(c => (
                  <th
                    key={c}
                    className="text-left px-2 py-1.5 border-b border-border sticky top-0 bg-surface whitespace-nowrap"
                  >
                    {c}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {rows.map((row, i) => (
                <tr key={i}>
                  {parsed.columns.map(c => (
                    <td key={c} className="px-2 py-1.5 border-b border-border align-top max-w-md break-all">
                      {String(row[c] ?? '')}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </CardBody>
    </Card>
  );
}
