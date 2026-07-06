import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import {
  RefreshCw,
  Plus,
  Database,
  Users,
  Check,
  AlertCircle,
  Plug,
  Activity,
  TrendingUp,
  Layers,
  Zap,
  ArrowLeftRight,
  Info,
  type LucideIcon,
} from 'lucide-react';
import {
  BeaconHero,
  Button,
  Card,
  CardHead,
  CardTitle,
  CardSub,
  CardActions,
  CardBody,
  Pill,
  KPIGrid,
  Seg,
} from '@/components/beacon';
import { formatNumber } from '@/lib/format';
import { useAuth } from '@/auth/useAuth';
import { Sparkline, LineChart, PerfHistogram, type PerfMetric } from '@/components/ui/charts';
import {
  useHomeTrendsQuery,
  useHomeActivityQuery,
  useExecutionUptimeQuery,
  type HomeActivityItem,
} from './queries';
import { HomeKpi, StatRow, FeedItem } from './atoms';
import { MigrationOverviewCard, TaskMgmtCard } from './sections';

// ── Time range helpers ────────────────────────────────────────────────────────

// The trends API granularity is days — a sub-day range can't be honest, so
// the smallest option is 24H (days: 1).
const RANGES = [
  { label: '24H', days: 1 },
  { label: '7D', days: 7 },
  { label: '30D', days: 30 },
  { label: '90D', days: 90 },
] as const;
type RangeLabel = (typeof RANGES)[number]['label'];

function relativeTime(ts: string): string {
  const diffMs = Date.now() - new Date(ts).getTime();
  const secs = Math.floor(diffMs / 1000);
  if (secs < 0) return 'now'; // future timestamp (clock skew) — never render "-3s"
  if (secs < 60) return `${secs}s`;
  const mins = Math.floor(secs / 60);
  if (mins < 60) return `${mins}m`;
  const hrs = Math.floor(mins / 60);
  if (hrs < 24) return `${hrs}h`;
  return `${Math.floor(hrs / 24)}d`;
}


const feedIconMap: Record<string, LucideIcon> = {
  Info,
  Activity,
  Check,
  Plus,
  AlertCircle,
  Database,
  Users,
  ArrowLeftRight,
};

// ── Main page ─────────────────────────────────────────────────────────────────

export default function HomePage() {
  const [activeRange, setActiveRange] = useState<RangeLabel>('30D');
  const [perfMetric, setPerfMetric] = useState<PerfMetric>('avg');

  const days = useMemo(() => RANGES.find(r => r.label === activeRange)?.days ?? 30, [activeRange]);
  const rangeText = days === 1 ? 'last 24 hours' : `last ${days} days`;

  const { data: auth } = useAuth();
  const { data: trends, isLoading: trendsLoading, refetch: refetchTrends } = useHomeTrendsQuery(days);
  const { data: activity, isLoading: activityLoading, refetch: refetchActivity } = useHomeActivityQuery();
  const { data: uptime, refetch: refetchUptime } = useExecutionUptimeQuery(24);

  const handleRefresh = () => {
    void refetchTrends();
    void refetchActivity();
    void refetchUptime();
  };

  const dash = (v: string | number | undefined | null, fallback = '—') =>
    trendsLoading || v == null ? fallback : String(v);
  const fmtDelta = (n: number, suffix = '') => (n >= 0 ? `+${n}${suffix}` : `${n}${suffix}`);
  const fmtPct = (n: number) => (n >= 0 ? `+${n.toFixed(1)}%` : `${n.toFixed(1)}%`);

  const anomaliesOpen = trends?.anomaliesOpen ?? 0;
  const anomaliesAck = trends?.anomaliesAcknowledged ?? 0;
  const perfBuckets = trends?.perfBuckets ?? [];

  // Derive the system pill from the execution uptime ticks already loaded —
  // never hardcode a "Healthy" badge.
  const systemHealth = uptime?.ticks?.some(t => t === 'crit')
    ? { tone: 'crit' as const, label: 'Issues' }
    : uptime?.ticks?.some(t => t === 'warn')
      ? { tone: 'warn' as const, label: 'Degraded' }
      : uptime?.ticks
        ? { tone: 'ok' as const, label: 'Healthy' }
        : null;

  const firstName = auth?.displayName?.split(' ')[0]?.toLowerCase() ?? 'there';

  return (
    <div className="flex flex-col gap-5 p-7">
      <BeaconHero
        user={firstName}
        ticks={uptime?.ticks}
        meta={{
          executions30d: trends?.queryExecutions30d ?? 0,
          anomalies: anomaliesOpen,
          avgMs: trends ? Math.round(trends.avgExecutionMs) : 0,
        }}
        actions={
          <>
            <Button icon={<RefreshCw />} onClick={handleRefresh}>
              Refresh
            </Button>
            <Link to="/queries/new">
              <Button variant="primary" icon={<Plus />}>
                New query
              </Button>
            </Link>
          </>
        }
      />

      {/* Range bar */}
      <div className="flex flex-wrap items-center gap-2">
        <Seg
          value={activeRange}
          onChange={v => setActiveRange(v as RangeLabel)}
          options={RANGES.map(r => ({ value: r.label, label: r.label }))}
        />
      </div>

      {/* KPI grid with sparklines */}
      <KPIGrid>
        <HomeKpi
          dot="brand"
          label="Active subscriptions"
          value={dash(trends?.totalSubscriptions != null ? formatNumber(trends.totalSubscriptions) : null)}
          delta={trends ? fmtDelta(trends.subscriptionDelta) : undefined}
          deltaDir={trends && trends.subscriptionDelta >= 0 ? 'up' : 'down'}
          sub={`${trends?.dataSourcesOnline ?? '—'} sources online`}
          spark={
            !trendsLoading && trends?.subscriptionsSpark ? (
              <Sparkline points={trends.subscriptionsSpark} color="var(--brand-500)" />
            ) : undefined
          }
        />
        <HomeKpi
          dot="info"
          label="Query executions"
          value={dash(trends?.queryExecutions30d != null ? formatNumber(trends.queryExecutions30d) : null)}
          delta={trends ? fmtPct(trends.queryExecutionsDelta) : undefined}
          deltaDir={trends && trends.queryExecutionsDelta >= 0 ? 'up' : 'down'}
          sub={rangeText}
          spark={
            !trendsLoading && trends?.queriesSpark ? (
              <Sparkline points={trends.queriesSpark} color="var(--info)" />
            ) : undefined
          }
        />
        <HomeKpi
          dot="warn"
          label="Notifications sent"
          value={dash(trends?.notificationsSent30d != null ? formatNumber(trends.notificationsSent30d) : null)}
          delta={trends ? fmtDelta(trends.notificationsDelta) : undefined}
          deltaDir={trends && trends.notificationsDelta >= 0 ? 'up' : 'down'}
          sub={`${trends?.recipientsCount ?? '—'} recipients`}
          spark={
            !trendsLoading && trends?.notificationsSpark ? (
              <Sparkline points={trends.notificationsSpark} color="var(--warn)" />
            ) : undefined
          }
        />
        <HomeKpi
          dot="crit"
          label="Anomalies detected"
          value={dash(formatNumber(anomaliesOpen))}
          delta={trends ? fmtDelta(trends.anomaliesDelta) : undefined}
          deltaDir={trends && trends.anomaliesDelta <= 0 ? 'down' : 'up'}
          sub={
            <span className="inline-flex items-center gap-1.5">
              <Pill tone="crit">{anomaliesOpen} open</Pill>
              {anomaliesAck} acknowledged
            </span>
          }
          spark={
            !trendsLoading && trends?.anomaliesSpark ? (
              <Sparkline points={trends.anomaliesSpark} color="var(--crit)" />
            ) : undefined
          }
        />
      </KPIGrid>

      {/* Performance KPIs */}
      <KPIGrid>
        <HomeKpi
          dot="ok"
          label="Avg execution"
          value={trends ? Math.round(trends.avgExecutionMs).toLocaleString() : '—'}
          unit="ms"
          sub="across all queries"
          delta={trends ? fmtPct(trends.avgExecutionDeltaPct) : undefined}
          deltaDir={trends && trends.avgExecutionDeltaPct <= 0 ? 'down' : 'up'}
        />
        <HomeKpi
          dot="ok"
          label="Fastest query"
          value={trends?.fastestQueryMs != null ? Math.round(trends.fastestQueryMs).toLocaleString() : '—'}
          unit="ms"
          sub={
            trends?.fastestQueryName ? (
              <span className="mono">{trends.fastestQueryName}</span>
            ) : (
              <span className="text-text-muted">no data</span>
            )
          }
        />
        <HomeKpi
          dot="warn"
          label="Slowest query"
          value={trends?.slowestQueryMs != null ? Math.round(trends.slowestQueryMs).toLocaleString() : '—'}
          unit="ms"
          sub={
            trends?.slowestQueryName ? (
              <span className="mono">{trends.slowestQueryName}</span>
            ) : (
              <span className="text-text-muted">no data</span>
            )
          }
        />
      </KPIGrid>

      {/* Activity trends + system overview */}
      <div className="grid gap-5 lg:grid-cols-[minmax(0,7fr)_minmax(0,3fr)] items-start">
        <Card>
          <CardHead>
            <TrendingUp className="size-3.5 text-text-muted" />
            <CardTitle>Activity trends</CardTitle>
            <CardSub>{rangeText}</CardSub>
            <CardActions>
              <span className="inline-flex items-center gap-1.5 text-xs text-text-muted">
                <span className="size-2 rounded-xs bg-brand-500" /> Queries
              </span>
              <span className="inline-flex items-center gap-1.5 text-xs text-text-muted">
                <span className="size-2 rounded-xs bg-warn" />
                Notifications
              </span>
            </CardActions>
          </CardHead>
          <CardBody>
            <LineChart
              days={days}
              series={[
                { name: 'Queries', color: 'var(--brand-500)', data: trends?.queryTrend30d ?? [] },
                { name: 'Notifs', color: 'var(--warn)', data: trends?.notificationsTrend30d ?? [] },
              ]}
            />
          </CardBody>
        </Card>

        <Card>
          <CardHead>
            <Layers className="size-3.5 text-text-muted" />
            <CardTitle>System overview</CardTitle>
            <CardActions>
              {systemHealth && (
                <Pill tone={systemHealth.tone} dot>
                  {systemHealth.label}
                </Pill>
              )}
            </CardActions>
          </CardHead>
          <div>
            <StatRow
              Icon={Database}
              label="Data sources"
              value={trendsLoading ? '—' : formatNumber(trends?.dataSourcesOnline ?? 0)}
              trail={<Pill tone="ok">{trendsLoading ? '—' : trends?.dataSourcesOnline} online</Pill>}
            />
            <StatRow
              Icon={Users}
              label="Recipients"
              value={trendsLoading ? '—' : formatNumber(trends?.recipientsCount ?? 0)}
            />
            <StatRow
              Icon={Check}
              label="Active subscriptions"
              value={trendsLoading ? '—' : formatNumber(trends?.totalSubscriptions ?? 0)}
            />
            <StatRow
              Icon={AlertCircle}
              label="Active anomalies"
              value={trendsLoading ? '—' : formatNumber(anomaliesOpen)}
              trail={anomaliesOpen > 0 ? <Pill tone="crit">action req.</Pill> : undefined}
            />
            <StatRow
              Icon={Plug}
              label="Integrations"
              value={trendsLoading ? '—' : formatNumber(trends?.integrationsCount ?? 0)}
              trail={<span className="text-xs mono text-text-subtle">channel types</span>}
            />
          </div>
        </Card>
      </div>

      {/* Query exec performance + recent activity */}
      <div className="grid gap-5 lg:grid-cols-[minmax(0,7fr)_minmax(0,3fr)] items-stretch">
        <Card>
          <CardHead>
            <Zap className="size-3.5 text-text-muted" />
            <CardTitle>Query execution performance</CardTitle>
            <CardSub>
              {rangeText} · all sources
            </CardSub>
            <CardActions>
              <Seg
                value={perfMetric}
                onChange={v => setPerfMetric(v as PerfMetric)}
                options={[
                  { value: 'avg', label: 'avg' },
                  { value: 'p50', label: 'p50' },
                  { value: 'p95', label: 'p95' },
                  { value: 'p99', label: 'p99' },
                ]}
              />
            </CardActions>
          </CardHead>
          <CardBody>
            <PerfHistogram buckets={perfBuckets} metric={perfMetric} />
            <div className="flex items-center gap-4 mt-2 text-xs text-text-muted">
              <span className="inline-flex items-center gap-1.5">
                <span className="size-2 rounded-xs bg-brand-500" /> typical
              </span>
              <span className="inline-flex items-center gap-1.5">
                <span className="size-2 rounded-xs bg-warn" /> p95 spike
              </span>
              <span className="inline-flex items-center gap-1.5">
                <span className="size-2 rounded-xs bg-crit" /> p99 spike
              </span>
            </div>
          </CardBody>
        </Card>

        <Card>
          <CardHead>
            <Activity className="size-3.5 text-text-muted" />
            <CardTitle>Recent activity</CardTitle>
            <CardActions>
              <Link to="/control-tower" className="text-xs font-medium text-brand-600 hover:underline">
                View all →
              </Link>
            </CardActions>
          </CardHead>
          <div className="overflow-y-auto max-h-[340px]">
            {activityLoading && (
              <div className="px-4 py-5 text-sm text-text-subtle">Loading…</div>
            )}
            {!activityLoading && (!activity?.items || activity.items.length === 0) && (
              <div className="px-4 py-6 text-center">
                <Activity size={20} className="text-text-muted inline-block" />
                <p className="mt-2 text-sm text-text-subtle">No recent activity</p>
              </div>
            )}
            {!activityLoading &&
              activity?.items.map((item: HomeActivityItem, idx) => (
                <FeedItem
                  key={`${item.timestamp}-${item.title}-${idx}`}
                  tone={item.tone}
                  Icon={feedIconMap[item.icon] ?? Info}
                  title={item.title}
                  meta={item.meta ?? undefined}
                  time={relativeTime(item.timestamp)}
                />
              ))}
          </div>
        </Card>
      </div>

      {/* Bottom row: migration + tasks side by side */}
      <div className="grid gap-5 lg:grid-cols-2 items-stretch">
        <MigrationOverviewCard />
        <TaskMgmtCard />
      </div>
    </div>
  );
}
