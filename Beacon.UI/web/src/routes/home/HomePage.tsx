import { useMemo, useState, type ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import {
  Download,
  RefreshCw,
  Plus,
  Filter,
  Database,
  Users,
  Calendar,
  Check,
  AlertCircle,
  Plug,
  Activity,
  TrendingUp,
  Layers,
  Zap,
  ArrowLeftRight,
  ArrowDown,
  ArrowUp,
  ChevronRight,
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
  Kbd,
} from '@/components/beacon';
import { formatNumber } from '@/lib/format';
import { fetchJson } from '@/lib/api';
import { useAuth } from '@/auth/useAuth';
import { cn } from '@/lib/cn';
import {
  useHomeTrendsQuery,
  useHomeActivityQuery,
  type HomePerfBucket,
  type HomeActivityItem,
  type GetHomeMigrationSummaryResult,
  type GetHomeTaskSummaryResult,
} from './queries';

// ── Time range helpers ────────────────────────────────────────────────────────

const RANGES = [
  { label: '1H', days: 1 },
  { label: '24H', days: 1 },
  { label: '7D', days: 7 },
  { label: '30D', days: 30 },
  { label: '90D', days: 90 },
] as const;
type RangeLabel = (typeof RANGES)[number]['label'];

function relativeTime(ts: string): string {
  const diffMs = Date.now() - new Date(ts).getTime();
  const secs = Math.floor(diffMs / 1000);
  if (secs < 60) return `${secs}s`;
  const mins = Math.floor(secs / 60);
  if (mins < 60) return `${mins}m`;
  const hrs = Math.floor(mins / 60);
  if (hrs < 24) return `${hrs}h`;
  return `${Math.floor(hrs / 24)}d`;
}

// ── SVG chart components ──────────────────────────────────────────────────────

function Sparkline({
  points,
  color = 'var(--brand-500)',
  width = 88,
  height = 28,
}: {
  points: number[];
  color?: string;
  width?: number;
  height?: number;
}) {
  if (!points || points.length === 0) {
    return <svg width={width} height={height} />;
  }
  const max = Math.max(...points, 1);
  const min = Math.min(...points, 0);
  const range = max - min || 1;
  const step = points.length > 1 ? width / (points.length - 1) : width;
  const coords = points.map((v, i) => [i * step, height - ((v - min) / range) * (height - 4) - 2]);
  const d = coords.map((c, i) => (i ? 'L' : 'M') + c[0].toFixed(1) + ' ' + c[1].toFixed(1)).join(' ');
  const area = d + ` L ${width} ${height} L 0 ${height} Z`;
  const id = 'sg-' + color.replace(/[^a-z0-9]/gi, '').slice(0, 8) + '-' + points.length;
  return (
    <svg viewBox={`0 0 ${width} ${height}`} preserveAspectRatio="none" width={width} height={height}>
      <defs>
        <linearGradient id={id} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor={color} stopOpacity="0.18" />
          <stop offset="100%" stopColor={color} stopOpacity="0" />
        </linearGradient>
      </defs>
      <path d={area} fill={`url(#${id})`} />
      <path d={d} fill="none" stroke={color} strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

function LineChart({
  series,
  height = 240,
  days = 30,
}: {
  series: { name: string; color: string; data: number[] }[];
  height?: number;
  days?: number;
}) {
  const w = 1000;
  const h = height;
  const pad = { top: 16, right: 16, bottom: 28, left: 38 };
  const innerW = w - pad.left - pad.right;
  const innerH = h - pad.top - pad.bottom;
  const allMax = Math.max(...series.flatMap(s => s.data), 1);
  const allMin = 0;
  const range = allMax - allMin || 1;
  const step = innerW / Math.max(days - 1, 1);
  const xAt = (i: number) => pad.left + i * step;
  const yAt = (v: number) => pad.top + innerH - ((v - allMin) / range) * innerH;
  const yTicks = 4;
  const tickValues = Array.from({ length: yTicks + 1 }, (_, i) => Math.round((allMax / yTicks) * i));
  const xLabels = [0, Math.floor(days / 4), Math.floor(days / 2), Math.floor((days * 3) / 4), days - 1].filter(
    (v, i, a) => a.indexOf(v) === i,
  );

  return (
    <svg viewBox={`0 0 ${w} ${h}`} width="100%" height={h} className="block">
      {tickValues.map((v, i) => (
        <g key={i}>
          <line
            x1={pad.left}
            x2={w - pad.right}
            y1={yAt(v)}
            y2={yAt(v)}
            stroke="var(--border)"
            strokeDasharray={i === 0 ? '' : '2 4'}
          />
          <text x={pad.left - 8} y={yAt(v) + 3} fontSize="10.5" fill="var(--text-subtle)" fontFamily="var(--font-mono)" textAnchor="end">
            {v}
          </text>
        </g>
      ))}
      {xLabels.map(i => (
        <text key={i} x={xAt(i)} y={h - 8} fontSize="10.5" fill="var(--text-subtle)" fontFamily="var(--font-mono)" textAnchor="middle">
          {`d-${days - 1 - i}`}
        </text>
      ))}
      {series.map(s => {
        const safeData = s.data.length > 0 ? s.data : Array(days).fill(0);
        const id = 'lg-' + s.name.replace(/\s/g, '');
        const d = safeData.map((v, i) => (i ? 'L' : 'M') + xAt(i) + ' ' + yAt(v)).join(' ');
        const area = d + ` L ${xAt(safeData.length - 1)} ${pad.top + innerH} L ${xAt(0)} ${pad.top + innerH} Z`;
        return (
          <g key={s.name}>
            <defs>
              <linearGradient id={id} x1="0" y1="0" x2="0" y2="1">
                <stop offset="0%" stopColor={s.color} stopOpacity="0.18" />
                <stop offset="100%" stopColor={s.color} stopOpacity="0" />
              </linearGradient>
            </defs>
            <path d={area} fill={`url(#${id})`} />
            <path d={d} stroke={s.color} strokeWidth="1.8" fill="none" strokeLinecap="round" strokeLinejoin="round" />
          </g>
        );
      })}
    </svg>
  );
}

type PerfMetric = 'avg' | 'p50' | 'p95' | 'p99';

function PerfHistogram({
  buckets,
  metric = 'avg',
  height = 200,
}: {
  buckets: HomePerfBucket[];
  metric?: PerfMetric;
  height?: number;
}) {
  if (!buckets || buckets.length === 0) {
    return (
      <div className="flex items-center justify-center text-sm text-text-muted" style={{ height }}>
        No execution data yet
      </div>
    );
  }
  const w = 1000;
  const h = height;
  const pad = { top: 14, right: 16, bottom: 28, left: 38 };
  const innerW = w - pad.left - pad.right;
  const innerH = h - pad.top - pad.bottom;

  const getValue = (b: HomePerfBucket) => {
    if (metric === 'p50') return b.p50Ms;
    if (metric === 'p95') return b.p95Ms;
    if (metric === 'p99') return b.p99Ms;
    return b.avgMs;
  };

  const values = buckets.map(getValue);
  const max = Math.max(...values, 1);
  const bw = innerW / buckets.length;
  const p95Line = max * 0.65;

  return (
    <svg viewBox={`0 0 ${w} ${h}`} width="100%" height={h} className="block">
      {[0, 0.25, 0.5, 0.75, 1].map((t, i) => (
        <g key={i}>
          <line
            x1={pad.left}
            x2={w - pad.right}
            y1={pad.top + innerH * (1 - t)}
            y2={pad.top + innerH * (1 - t)}
            stroke="var(--border)"
            strokeDasharray={t === 0 ? '' : '2 4'}
          />
          <text
            x={pad.left - 8}
            y={pad.top + innerH * (1 - t) + 3}
            fontSize="10.5"
            fill="var(--text-subtle)"
            fontFamily="var(--font-mono)"
            textAnchor="end"
          >
            {Math.round(max * t)}ms
          </text>
        </g>
      ))}
      {buckets.map((b, i) => {
        const val = getValue(b);
        const bh = (val / max) * innerH;
        const x = pad.left + i * bw + 2;
        const y = pad.top + innerH - bh;
        const color =
          b.p99Ms > 0 && val === b.p99Ms && metric === 'p99'
            ? 'var(--crit)'
            : b.p95Ms > 0 && val === b.p95Ms && metric === 'p95'
              ? 'var(--warn)'
              : 'var(--brand-500)';
        return (
          <g key={i}>
            <rect x={x} y={y} width={bw - 4} height={bh} rx="2" fill={color} opacity="0.85" />
            {i % Math.max(1, Math.floor(buckets.length / 10)) === 0 && (
              <text
                x={x + (bw - 4) / 2}
                y={h - 10}
                fontSize="10.5"
                fill="var(--text-subtle)"
                fontFamily="var(--font-mono)"
                textAnchor="middle"
              >
                {b.label}
              </text>
            )}
          </g>
        );
      })}
      {max > 0 && (
        <>
          <line
            x1={pad.left}
            x2={w - pad.right}
            y1={pad.top + innerH * (1 - p95Line / max)}
            y2={pad.top + innerH * (1 - p95Line / max)}
            stroke="var(--warn)"
            strokeDasharray="3 3"
            opacity="0.7"
          />
          <text
            x={w - pad.right - 4}
            y={pad.top + innerH * (1 - p95Line / max) - 4}
            fontSize="10.5"
            fill="var(--warn)"
            fontFamily="var(--font-mono)"
            textAnchor="end"
          >
            p95 · {Math.round(p95Line)}ms
          </text>
        </>
      )}
    </svg>
  );
}

// ── KPI tile with sparkline + delta ──────────────────────────────────────────

type DotTone = 'brand' | 'ok' | 'warn' | 'crit' | 'info';

const dotClass: Record<DotTone, string> = {
  brand: 'bg-brand-500',
  ok: 'bg-ok',
  warn: 'bg-warn',
  crit: 'bg-crit',
  info: 'bg-info',
};

function HomeKpi({
  dot = 'brand',
  label,
  value,
  unit,
  sub,
  delta,
  deltaDir,
  spark,
}: {
  dot?: DotTone;
  label: string;
  value: ReactNode;
  unit?: string;
  sub?: ReactNode;
  delta?: string;
  deltaDir?: 'up' | 'down';
  spark?: ReactNode;
}) {
  const deltaIsImprovement = deltaDir === 'down';
  return (
    <div className="bg-surface border border-border rounded-md p-4 flex flex-col gap-1.5 min-w-0">
      <div className="flex items-center gap-2">
        <span className={cn('inline-block w-1.5 h-1.5 rounded-full', dotClass[dot])} />
        <span className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">{label}</span>
      </div>
      <div className="flex items-baseline gap-1 mt-0.5">
        <span className="text-[26px] font-semibold leading-none tracking-tighter">{value}</span>
        {unit && <span className="text-sm text-text-muted">{unit}</span>}
      </div>
      <div className="flex items-center gap-2 text-xs text-text-muted mt-0.5">
        {delta && (
          <span
            className={cn(
              'inline-flex items-center gap-0.5 mono',
              deltaIsImprovement ? 'text-ok' : 'text-text-muted',
            )}
          >
            {deltaDir === 'up' ? <ArrowUp size={11} /> : <ArrowDown size={11} />}
            {delta}
          </span>
        )}
        {sub && <span>{sub}</span>}
      </div>
      {spark && <div className="-mb-1 -mx-1 mt-1">{spark}</div>}
    </div>
  );
}

// ── Stat row used inside System Overview card ────────────────────────────────

function StatRow({
  Icon,
  label,
  value,
  trail,
}: {
  Icon: LucideIcon;
  label: string;
  value: string;
  trail?: ReactNode;
}) {
  return (
    <div className="flex items-center gap-3 px-4 py-2.5 border-b border-border last:border-b-0 hover:bg-surface-2 transition">
      <div className="size-7 rounded-sm bg-surface-2 grid place-items-center text-text-muted">
        <Icon size={15} />
      </div>
      <div className="flex-1 min-w-0">
        <div className="text-xs text-text-muted">{label}</div>
        <div className="text-sm font-medium mono">{value}</div>
      </div>
      {trail}
      <ChevronRight size={14} className="text-text-subtle" />
    </div>
  );
}

// ── Mini bar used inside Migration / Task cards ──────────────────────────────

function Mini({
  color,
  label,
  value,
  bar,
}: {
  color: string;
  label: string;
  value: string;
  bar: string;
}) {
  return (
    <div className="flex flex-col gap-1.5 p-4 border-r border-border last:border-r-0">
      <div className="flex items-center gap-1.5 text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">
        <span className="inline-block w-1.5 h-1.5 rounded-full" style={{ background: color }} />
        {label}
      </div>
      <div className="text-xl font-semibold mono tracking-tighter">{value}</div>
      <div className="h-1 rounded-full bg-surface-2 overflow-hidden">
        <span className="block h-full rounded-full" style={{ width: bar, background: color }} />
      </div>
    </div>
  );
}

// ── Feed item for recent activity ────────────────────────────────────────────

const feedTone: Record<string, { bg: string; fg: string }> = {
  ok: { bg: 'bg-ok-bg', fg: 'text-ok' },
  warn: { bg: 'bg-warn-bg', fg: 'text-warn' },
  crit: { bg: 'bg-crit-bg', fg: 'text-crit' },
  info: { bg: 'bg-info-bg', fg: 'text-info' },
  brand: { bg: 'bg-brand-50', fg: 'text-brand-600' },
};

function FeedItem({
  tone = 'info',
  Icon = Info,
  title,
  meta,
  time,
}: {
  tone?: string;
  Icon?: LucideIcon;
  title: string;
  meta?: string | null;
  time: string;
}) {
  const c = feedTone[tone] ?? feedTone.info;
  return (
    <div className="flex items-start gap-2.5 px-4 py-2.5 border-b border-border last:border-b-0">
      <div className={cn('shrink-0 size-6 grid place-items-center rounded-sm', c.bg, c.fg)}>
        <Icon size={12} />
      </div>
      <div className="flex-1 min-w-0">
        <div className="text-sm text-text">{title}</div>
        {meta && <div className="text-xs text-text-muted mt-0.5">{meta}</div>}
      </div>
      <div className="text-2xs text-text-subtle mono whitespace-nowrap pt-0.5">{time}</div>
    </div>
  );
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

  const { data: auth } = useAuth();
  const { data: trends, isLoading: trendsLoading, refetch: refetchTrends } = useHomeTrendsQuery(days);
  const { data: activity, isLoading: activityLoading, refetch: refetchActivity } = useHomeActivityQuery();

  const handleRefresh = () => {
    void refetchTrends();
    void refetchActivity();
  };

  const dash = (v: string | number | undefined | null, fallback = '—') =>
    trendsLoading || v == null ? fallback : String(v);
  const fmtDelta = (n: number, suffix = '') => (n >= 0 ? `+${n}${suffix}` : `${n}${suffix}`);
  const fmtPct = (n: number) => (n >= 0 ? `+${n.toFixed(1)}%` : `${n.toFixed(1)}%`);

  const anomaliesOpen = trends?.anomaliesOpen ?? 0;
  const anomaliesAck = trends?.anomaliesAcknowledged ?? 0;
  const perfBuckets = trends?.perfBuckets ?? [];

  const firstName = auth?.displayName?.split(' ')[0]?.toLowerCase() ?? 'there';

  return (
    <div className="flex flex-col gap-5 p-7">
      <BeaconHero
        user={firstName}
        meta={{
          executions30d: trends?.queryExecutions30d ?? 0,
          anomalies: anomaliesOpen,
          p50ms: trends ? Math.round(trends.avgExecutionMs) : 0,
        }}
        actions={
          <>
            <Button variant="ghost" icon={<Download />}>
              Export
            </Button>
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

      {/* Filter / range bar */}
      <div className="flex flex-wrap items-center gap-2">
        <Seg
          value={activeRange}
          onChange={v => setActiveRange(v as RangeLabel)}
          options={RANGES.map(r => ({ value: r.label, label: r.label }))}
        />
        <div className="w-px h-5 bg-border mx-1" />
        <Button size="sm" variant="ghost" icon={<Filter />}>
          All projects
        </Button>
        <Button size="sm" variant="ghost" icon={<Database />}>
          All sources
        </Button>
        <Button size="sm" variant="ghost" icon={<Users />}>
          All users
        </Button>
        <div className="flex-1" />
        <Button size="sm" variant="ghost" icon={<Calendar />} aria-label="Calendar" />
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
          sub={`last ${days} days`}
          spark={
            !trendsLoading && trends?.queriesSpark ? (
              <Sparkline points={trends.queriesSpark} color="oklch(60% 0.13 240)" />
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
              <Sparkline points={trends.notificationsSpark} color="oklch(70% 0.15 70)" />
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
              <Sparkline points={trends.anomaliesSpark} color="oklch(60% 0.19 25)" />
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
          value={trends?.fastestQueryMs ? Math.round(trends.fastestQueryMs).toLocaleString() : '—'}
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
          value={trends?.slowestQueryMs ? Math.round(trends.slowestQueryMs).toLocaleString() : '—'}
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
            <CardSub>last {days} days</CardSub>
            <CardActions>
              <span className="inline-flex items-center gap-1.5 text-xs text-text-muted">
                <span className="size-2 rounded-xs bg-brand-500" /> Queries
              </span>
              <span className="inline-flex items-center gap-1.5 text-xs text-text-muted">
                <span className="size-2 rounded-xs" style={{ background: 'oklch(70% 0.15 70)' }} />
                Notifications
              </span>
            </CardActions>
          </CardHead>
          <CardBody>
            <LineChart
              days={days}
              series={[
                { name: 'Queries', color: 'var(--brand-500)', data: trends?.queryTrend30d ?? [] },
                { name: 'Notifs', color: 'oklch(70% 0.15 70)', data: trends?.notificationsTrend30d ?? [] },
              ]}
            />
          </CardBody>
        </Card>

        <Card>
          <CardHead>
            <Layers className="size-3.5 text-text-muted" />
            <CardTitle>System overview</CardTitle>
            <CardActions>
              <Pill tone="ok" dot>
                Healthy
              </Pill>
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
              last {days} days · all sources
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
              activity?.items.map((item: HomeActivityItem, i: number) => (
                <FeedItem
                  key={i}
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

      <div className="flex items-center justify-between text-xs mono text-text-subtle pt-1">
        <span>Beacon · region eu-west</span>
        <span>
          Press <Kbd>⌘</Kbd> <Kbd>K</Kbd> to search
        </span>
      </div>
    </div>
  );
}

// ── Migration overview card ───────────────────────────────────────────────────

function MigrationOverviewCard() {
  const { data, isLoading } = useHomeMigrationSummaryQuery();

  const total = data?.total ?? 0;
  const successful = data?.successful ?? 0;
  const executions = data?.executions ?? 0;
  const errored = data?.errored ?? 0;
  const successPct = total > 0 ? Math.round((successful / total) * 100) + '%' : '0%';
  const execPct = total > 0 ? Math.min(100, Math.round((executions / Math.max(total * 10, 1)) * 100)) + '%' : '0%';
  const errorPct = total > 0 ? Math.round((errored / total) * 100) + '%' : '0%';

  return (
    <Card>
      <CardHead>
        <ArrowLeftRight className="size-3.5 text-text-muted" />
        <CardTitle>Data migration</CardTitle>
        <CardSub>overview</CardSub>
        <CardActions>
          <Link
            to="/migration-history"
            className="text-xs font-medium text-brand-600 hover:underline"
          >
            Open jobs →
          </Link>
        </CardActions>
      </CardHead>
      <CardBody flush>
        <div className="grid grid-cols-2 sm:grid-cols-4">
          <Mini color="var(--info)" label="Total jobs" value={isLoading ? '—' : formatNumber(total)} bar="100%" />
          <Mini color="var(--ok)" label="Successful" value={isLoading ? '—' : formatNumber(successful)} bar={successPct} />
          <Mini
            color="var(--brand-500)"
            label="Executions"
            value={isLoading ? '—' : formatNumber(executions)}
            bar={execPct}
          />
          <Mini color="var(--crit)" label="Errored" value={isLoading ? '—' : formatNumber(errored)} bar={errorPct} />
        </div>
      </CardBody>
    </Card>
  );
}

function TaskMgmtCard() {
  const { data, isLoading } = useHomeTaskSummaryQuery();

  const total = data?.total ?? 0;
  const open = data?.open ?? 0;
  const resolved = data?.resolved ?? 0;
  const openPct = total > 0 ? Math.round((open / total) * 100) + '%' : '0%';
  const resolvedPct = total > 0 ? Math.round((resolved / total) * 100) + '%' : '0%';

  return (
    <Card>
      <CardHead>
        <Check className="size-3.5 text-text-muted" />
        <CardTitle>Task management</CardTitle>
        <CardSub>{isLoading ? '—' : `${open} unresolved`}</CardSub>
      </CardHead>
      <CardBody flush>
        <div className="grid grid-cols-3">
          <Mini color="var(--text-muted)" label="Total" value={isLoading ? '—' : formatNumber(total)} bar="100%" />
          <Mini color="var(--warn)" label="Open" value={isLoading ? '—' : formatNumber(open)} bar={openPct} />
          <Mini color="var(--ok)" label="Resolved" value={isLoading ? '—' : formatNumber(resolved)} bar={resolvedPct} />
        </div>
      </CardBody>
    </Card>
  );
}

// ── Local query hooks (kept here to match the existing data layer) ───────────

function useHomeMigrationSummaryQuery() {
  return useQuery({
    queryKey: ['home', 'migration-summary'],
    queryFn: () => fetchJson<GetHomeMigrationSummaryResult>('/beacon/api/home/migration-summary'),
    retry: false,
  });
}

function useHomeTaskSummaryQuery() {
  return useQuery({
    queryKey: ['home', 'task-summary'],
    queryFn: () => fetchJson<GetHomeTaskSummaryResult>('/beacon/api/home/task-summary'),
    retry: false,
  });
}
