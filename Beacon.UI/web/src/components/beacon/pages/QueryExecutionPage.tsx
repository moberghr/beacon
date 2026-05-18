/**
 * Sample page — Query Execution detail.
 *
 * Reference composition for PageHeader + KPI grid + a main/aside layout with
 * cards, timeline, and perf bars. Data is hard-coded for the demo.
 */
import * as React from 'react';
import {
  Bell,
  Bolt,
  Copy,
  Download,
  Inbox,
  Activity,
  Info,
  Lightbulb,
  RefreshCw,
} from 'lucide-react';

import {
  PageHeader,
  Button,
  Pill,
  KPI,
  KPIGrid,
  Card,
  CardHead,
  CardTitle,
  CardSub,
  CardActions,
  CardBody,
  Seg,
} from '@/components/beacon';
import { cn } from '@/lib/cn';

const cols = [
  'id',
  'subscription_id',
  'result_count',
  'compiled_sql',
  'notification_status',
  'execution_time_ms',
  'results',
  'created_time',
  'comment',
];
type Row = {
  id: number;
  subscription_id: number;
  result_count: number;
  compiled_sql: string;
  notification_status: 1 | 2 | 3 | 4;
  execution_time_ms: number;
  results: string;
  created_time: string;
  comment: string | null;
};
const rows: Row[] = [
  {
    id: 1,
    subscription_id: 1,
    result_count: 0,
    compiled_sql: 'select * from semantico.query_execution_history',
    notification_status: 4,
    execution_time_ms: 1.277,
    results: '[]',
    created_time: '2026-05-05T13:08:30.904527Z',
    comment: null,
  },
  {
    id: 2,
    subscription_id: 1,
    result_count: 1,
    compiled_sql: 'select * from semantico.query_execution_history',
    notification_status: 2,
    execution_time_ms: 3.5335,
    results: '[{...}]',
    created_time: '2026-05-05T13:08:34.050638Z',
    comment: null,
  },
];

export function QueryExecutionPage() {
  const [tab, setTab] = React.useState<'table' | 'json'>('table');

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        variant="signal"
        eyebrow={
          <>
            <a href="#" className="hover:text-text">
              Query execution history
            </a>
            <span className="eyebrow-sep">/</span>
            <span className="mono normal-case tracking-normal">#3</span>
            <span className="eyebrow-sep">·</span>
            <Pill tone="ok" dot>
              NotificationSent
            </Pill>
          </>
        }
        prefix="Execution"
        emphasis="#3"
        sub={
          <>
            From query{' '}
            <a href="#" className="mono text-brand-600">
              qeh select
            </a>{' '}
            · subscription <span className="mono">#1</span> · executed{' '}
            <span className="mono">07 May 2026 · 08:14:39 UTC</span>
          </>
        }
        actions={
          <>
            <Button icon={<Download />}>Download JSON</Button>
            <Button icon={<RefreshCw />}>Re-run</Button>
            <Button variant="primary" icon={<Bell />}>
              Resend notification
            </Button>
          </>
        }
      />

      <KPIGrid>
        <KPI
          dot="ok"
          label="Execution time"
          value={
            <>
              6<span className="text-sm text-text-muted ml-1">ms</span>
            </>
          }
          sub="under p50 (12ms)"
        />
        <KPI dot="brand" label="Result count" value="2" sub="rows returned" />
        <KPI dot="info" label="Notifications" value="0" sub={<Pill>none sent</Pill>} />
        <KPI dot="warn" label="Tasks" value="1" sub={<Pill tone="warn">1 open</Pill>} />
      </KPIGrid>

      <div className="grid gap-5 lg:grid-cols-[minmax(0,1fr)_320px] items-start">
        <div className="flex flex-col gap-5 min-w-0">
          <Card>
            <CardHead>
              <Info className="size-3.5 text-text-muted" />
              <CardTitle>Execution information</CardTitle>
              <CardActions>
                <Pill tone="ok" dot>
                  NotificationSent
                </Pill>
              </CardActions>
            </CardHead>
            <CardBody>
              <dl className="grid grid-cols-1 sm:grid-cols-2 gap-x-6 gap-y-2 text-sm">
                <KV label="Execution ID" value={<span className="mono">#3</span>} />
                <KV
                  label="Status"
                  value={
                    <Pill tone="ok" dot>
                      NotificationSent
                    </Pill>
                  }
                />
                <KV
                  label="Query"
                  value={
                    <a href="#" className="mono text-brand-600">
                      qeh select
                    </a>
                  }
                />
                <KV
                  label="Subscription"
                  value={<span className="mono">#1 · daily 08:00 UTC</span>}
                />
                <KV
                  label="Executed at"
                  value={<span className="mono">2026-05-07 · 08:14:39 UTC</span>}
                />
                <KV label="Execution time" value={<span className="mono">5.77 ms</span>} />
                <KV label="Triggered by" value={<span className="mono">cron · scheduler</span>} />
                <KV label="Recipients" value={<span className="mono">0 reached</span>} />
              </dl>
            </CardBody>
          </Card>

          <Card>
            <CardHead>
              <Bolt className="size-3.5 text-text-muted" />
              <CardTitle>Compiled SQL</CardTitle>
              <CardSub>resolved · postgres dialect</CardSub>
              <CardActions>
                <Button variant="ghost" size="sm" icon={<Copy />}>
                  Copy
                </Button>
                <Button variant="ghost" size="sm" icon={<Download />}>
                  Download .sql
                </Button>
              </CardActions>
            </CardHead>
            <CardBody flush>
              <div className="bg-surface-2 border-y border-border">
                <div className="flex items-center gap-2 px-3 py-1.5 text-2xs text-text-muted border-b border-border bg-surface">
                  <Bolt className="size-3" />
                  <span className="mono normal-case tracking-normal">compiled.sql</span>
                  <span className="ml-auto subtle">postgres · ro</span>
                </div>
                <pre className="m-0 px-4 py-3 mono text-xs leading-relaxed overflow-x-auto">
                  <span className="tok-kw">SELECT</span>
                  {' * '}
                  <span className="tok-kw">FROM</span>
                  {' semantico.query_execution_history'}
                </pre>
                <div className="flex items-center gap-3 px-4 py-1.5 text-2xs text-text-muted border-t border-border">
                  <span className="text-ok">● 6ms</span>
                  <span>1 line · 47 chars</span>
                  <span className="ml-auto">2 rows · 0 errors</span>
                </div>
              </div>
            </CardBody>
          </Card>

          <Card>
            <CardHead>
              <Inbox className="size-3.5 text-text-muted" />
              <CardTitle>Query results</CardTitle>
              <CardSub>
                <span className="mono">2 rows</span>
              </CardSub>
              <CardActions>
                <Seg
                  value={tab}
                  onChange={setTab}
                  options={[
                    { value: 'table', label: 'Table' },
                    { value: 'json', label: 'Raw JSON' },
                  ]}
                />
                <Button size="sm" icon={<Copy />}>
                  Copy results
                </Button>
              </CardActions>
            </CardHead>
            <CardBody flush>
              {tab === 'table' ? (
                <div className="overflow-x-auto">
                  <table className="w-full border-collapse text-xs">
                    <thead>
                      <tr>
                        {cols.map(c => (
                          <th
                            key={c}
                            className="text-left px-3.5 py-2.5 mono font-semibold uppercase tracking-eyebrow text-text-muted bg-surface-2 border-b border-border whitespace-nowrap"
                          >
                            {c}
                          </th>
                        ))}
                      </tr>
                    </thead>
                    <tbody>
                      {rows.map(r => (
                        <tr key={r.id} className="hover:bg-surface-2">
                          <td className="px-3.5 py-2.5 mono border-b border-border">{r.id}</td>
                          <td className="px-3.5 py-2.5 mono border-b border-border">
                            {r.subscription_id}
                          </td>
                          <td className="px-3.5 py-2.5 mono border-b border-border">
                            <span
                              className={cn(
                                'tabular-nums',
                                r.result_count > 0 ? 'font-medium' : 'text-text-subtle',
                              )}
                            >
                              {r.result_count}
                            </span>
                          </td>
                          <td className="px-3.5 py-2.5 mono border-b border-border text-text-subtle max-w-[240px] truncate">
                            {r.compiled_sql}
                          </td>
                          <td className="px-3.5 py-2.5 border-b border-border">
                            <NotifStatus n={r.notification_status} />
                          </td>
                          <td className="px-3.5 py-2.5 mono border-b border-border">
                            {r.execution_time_ms.toFixed(3)}
                          </td>
                          <td className="px-3.5 py-2.5 mono border-b border-border text-text-subtle max-w-[180px] truncate">
                            {r.results}
                          </td>
                          <td className="px-3.5 py-2.5 mono border-b border-border text-text-subtle">
                            {r.created_time}
                          </td>
                          <td className="px-3.5 py-2.5 mono border-b border-border text-text-subtle">
                            {r.comment ?? '—'}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              ) : (
                <pre className="m-0 p-4 bg-surface-2 mono text-xs leading-relaxed overflow-x-auto max-h-[420px]">
                  {JSON.stringify(rows, null, 2)}
                </pre>
              )}
            </CardBody>
          </Card>
        </div>

        <aside className="flex flex-col gap-5">
          <Card>
            <CardHead>
              <Activity className="size-3.5 text-text-muted" />
              <CardTitle>Execution timeline</CardTitle>
            </CardHead>
            <div className="py-2">
              <TLItem time="08:14:39.000" tone="ok" title="Execution started" sub="triggered by cron" />
              <TLItem time="08:14:39.002" tone="ok" title="SQL compiled" sub="postgres · 2ms" />
              <TLItem
                time="08:14:39.006"
                tone="ok"
                title="Query returned 2 rows"
                sub="6ms total · under p50"
              />
              <TLItem
                time="08:14:39.014"
                tone="info"
                title="1 task created"
                sub="open · awaiting review"
              />
              <TLItem
                time="08:14:39.014"
                tone="ok"
                title="Notification sent"
                sub="webhook · 200 OK"
                last
              />
            </div>
          </Card>

          <Card>
            <CardHead>
              <Activity className="size-3.5 text-text-muted" />
              <CardTitle>Performance</CardTitle>
              <CardSub>vs last 30 runs</CardSub>
            </CardHead>
            <CardBody className="flex flex-col gap-1.5">
              <PerfRow label="This run" pct={30} val="6 ms" tone="bg-ok" />
              <PerfRow label="p50" pct={60} val="12 ms" tone="bg-text-subtle" />
              <PerfRow label="p95" pct={92} val="18 ms" tone="bg-warn" />
            </CardBody>
          </Card>

          <div className="flex items-start gap-3 p-3.5 rounded-md border border-info/30 bg-info-bg">
            <Lightbulb className="size-4 text-info shrink-0 mt-0.5" />
            <div className="text-sm">
              <div className="font-medium">Tip · diff against</div>
              <div className="text-xs text-text-muted mt-0.5">
                Compare this execution to the previous run from the history page.
              </div>
            </div>
          </div>
        </aside>
      </div>
    </div>
  );
}

export default QueryExecutionPage;

function KV({ label, value }: { label: React.ReactNode; value: React.ReactNode }) {
  return (
    <div className="flex items-start justify-between gap-3 py-1 border-b border-dashed border-border last:border-b-0">
      <dt className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">{label}</dt>
      <dd className="text-sm text-right">{value}</dd>
    </div>
  );
}

function NotifStatus({ n }: { n: 1 | 2 | 3 | 4 }) {
  const map = {
    1: { tone: 'neutral' as const, label: 'pending' },
    2: { tone: 'ok' as const, label: 'sent' },
    3: { tone: 'info' as const, label: 'queued' },
    4: { tone: 'warn' as const, label: 'skipped' },
  };
  const s = map[n];
  return (
    <Pill tone={s.tone} dot>
      {s.label}
    </Pill>
  );
}

function TLItem({
  time,
  tone,
  title,
  sub,
  last,
}: {
  time: string;
  tone: 'ok' | 'info' | 'warn' | 'crit';
  title: React.ReactNode;
  sub?: React.ReactNode;
  last?: boolean;
}) {
  const dotClass = {
    ok: 'bg-ok',
    info: 'bg-info',
    warn: 'bg-warn',
    crit: 'bg-crit',
  }[tone];
  return (
    <div className="grid grid-cols-[28px_1fr_auto] gap-2 px-4 pb-3 items-start">
      <div className="relative h-full pt-1 flex justify-center">
        {!last && <span className="absolute top-3.5 -bottom-3 left-1/2 w-px bg-border" />}
        <span
          className={cn(
            'size-2.5 rounded-full relative z-10 ring-2 ring-surface shadow-[0_0_0_1px_var(--border)]',
            dotClass,
          )}
        />
      </div>
      <div>
        <div className="text-sm font-medium">{title}</div>
        {sub && <div className="text-xs text-text-muted mt-0.5">{sub}</div>}
      </div>
      <div className="text-2xs text-text-subtle mono whitespace-nowrap pt-0.5">{time}</div>
    </div>
  );
}

function PerfRow({
  label,
  pct,
  val,
  tone,
}: {
  label: string;
  pct: number;
  val: string;
  tone: string;
}) {
  return (
    <div className="grid grid-cols-[60px_1fr_60px] items-center gap-2.5 py-1.5">
      <div className="text-xs text-text-muted">{label}</div>
      <div className="h-1.5 bg-surface-2 border border-border rounded-full overflow-hidden">
        <div className={cn('h-full rounded-full', tone)} style={{ width: `${pct}%` }} />
      </div>
      <div className="text-xs text-right mono">{val}</div>
    </div>
  );
}
