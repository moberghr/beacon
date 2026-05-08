import { useMemo } from 'react';
import { Link, useParams } from 'react-router-dom';
import { Icon } from '@/components/Icon';
import { PageHeader } from '@/components/layout/PageHeader';
import { EmptyState } from '@/components/data/EmptyState';
import { formatDateTime, formatNumber } from '@/lib/format';
import { useNotificationDetailQuery } from './queries';

const STATUS_LABELS: Record<number, { label: string; cls: string }> = {
  1: { label: 'Created', cls: 'pill' },
  2: { label: 'Sent', cls: 'pill pill--ok' },
  3: { label: 'Silenced', cls: 'pill' },
  4: { label: 'No results', cls: 'pill pill--warn' },
  5: { label: 'Timeout', cls: 'pill pill--crit' },
  6: { label: 'Below threshold', cls: 'pill' },
  7: { label: 'Failed', cls: 'pill pill--crit' },
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
      <div className="page">
        <PageHeader title="Notification" />
        <EmptyState icon={<Icon.Alert size={20} />} title="Invalid notification id" description={String(id)} />
      </div>
    );
  }

  if (isLoading) {
    return (
      <div className="page">
        <PageHeader title="Notification" sub={<span className="muted">Loading…</span>} />
      </div>
    );
  }

  if (isError) {
    return (
      <div className="page">
        <PageHeader title="Notification" />
        <EmptyState
          icon={<Icon.Alert size={20} />}
          title="Failed to load notification"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      </div>
    );
  }

  if (entry === null) {
    return (
      <div className="page">
        <PageHeader
          title="Notification"
          sub={<Link to="/notifications" className="muted">← back to notifications</Link>}
        />
        <EmptyState icon={<Icon.Bell size={20} />} title="Notification not found" />
      </div>
    );
  }

  const status = STATUS_LABELS[entry.status] ?? { label: String(entry.status), cls: 'pill' };
  const typeLabel = TYPE_LABELS[entry.type] ?? String(entry.type);

  return (
    <div className="page">
      <PageHeader
        title={`Notification #${entry.id}`}
        sub={
          <span className="muted">
            <Link to="/notifications" className="muted">Notifications</Link>
            <span style={{ margin: '0 6px' }}>/</span>
            #{entry.id}
          </span>
        }
        actions={
          <span className={status.cls}>{status.label}</span>
        }
      />

      <div className="card" style={{ marginBottom: 16 }}>
        <div className="card__body">
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: 16 }}>
            <Section title="Notification">
              <Field label="Type" value={typeLabel} />
              <Field label="Created" value={formatDateTime(entry.createdTime)} />
              <Field label="Sent at" value={formatDateTime(entry.sentAt)} />
            </Section>

            <Section title="Recipient">
              <Field label="Name" value={entry.recipientName} />
            </Section>

            <Section title="Query">
              <Field label="Name" value={
                <a className="muted" href={`/beacon/queries/${entry.queryId}`}>{entry.queryName}</a>
              } />
              <Field label="Subscription" value={
                <a className="muted" href={`/beacon/subscriptions/${entry.subscriptionId}`}>
                  #{entry.subscriptionId}
                </a>
              } />
              <Field label="Execution time" value={`${entry.executionTimeMs.toFixed(2)} ms`} />
              <Field label="Result count" value={entry.resultCount === null ? '—' : formatNumber(entry.resultCount)} />
            </Section>
          </div>
        </div>
      </div>

      <ResultsCard rawResults={entry.results} parsed={parsed} />
    </div>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div>
      <h3 className="card__title" style={{ margin: '0 0 8px' }}>{title}</h3>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        {children}
      </div>
    </div>
  );
}

function Field({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div style={{ fontSize: 13 }}>
      <span className="muted" style={{ marginRight: 6 }}>{label}:</span>
      <span style={{ color: 'var(--text)' }}>{value}</span>
    </div>
  );
}

function ResultsCard({ rawResults, parsed }: { rawResults: string | null; parsed: ParsedResults | null }) {
  if (rawResults === null || rawResults.trim().length === 0) {
    return (
      <div className="card">
        <div className="card__body">
          <span className="muted">No results were stored for this notification.</span>
        </div>
      </div>
    );
  }

  if (parsed === null) {
    return (
      <div className="card">
        <div className="card__body">
          <h3 className="card__title" style={{ margin: '0 0 8px' }}>Stored results</h3>
          <pre
            className="mono"
            style={{
              fontSize: 12,
              padding: 12,
              background: 'var(--surface-2)',
              borderRadius: 6,
              maxHeight: 400,
              overflow: 'auto',
              margin: 0,
            }}
          >
            {rawResults}
          </pre>
        </div>
      </div>
    );
  }

  const truncated = parsed.rows.length > 100;
  const rows = truncated ? parsed.rows.slice(0, 100) : parsed.rows;

  return (
    <div className="card">
      <div className="card__body">
        <h3 className="card__title" style={{ margin: '0 0 8px' }}>
          Stored results <span className="muted" style={{ fontWeight: 400, fontSize: 12 }}>
            ({formatNumber(parsed.rows.length)} rows{truncated ? ' — showing first 100' : ''})
          </span>
        </h3>
        <div style={{ overflow: 'auto', maxHeight: 480 }}>
          <table className="result-table" style={{ width: '100%', borderCollapse: 'collapse', fontSize: 12 }}>
            <thead>
              <tr>
                {parsed.columns.map(c => (
                  <th
                    key={c}
                    style={{
                      textAlign: 'left',
                      padding: '6px 8px',
                      borderBottom: '1px solid var(--border)',
                      position: 'sticky',
                      top: 0,
                      background: 'var(--surface)',
                    }}
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
                    <td key={c} style={{ padding: '6px 8px', borderBottom: '1px solid var(--border)' }}>
                      {String(row[c] ?? '')}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}
