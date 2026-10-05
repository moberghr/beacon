import { useEffect, useState } from 'react';
import {
  Activity,
  AlertTriangle,
  ListChecks,
  RefreshCw,
  Search,
  Sparkles,
} from 'lucide-react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import {
  Button,
  Pill,
  KPI,
  KPIGrid,
  Card,
  Input,
  PageHeader,
  Select,
  Seg,
  type SegOption,
} from '@/components/beacon';
import { formatNumber, formatPercentage, formatRelativeTime } from '@/lib/format';
import { HealthStatus, NotificationStatus } from '@/lib/enums';
import { type AnomalySparklinePoint, type ControlTowerSubscriptionHealthData } from './api';
import { useControlTowerList, useControlTowerStatistics, useQueryFolders } from './queries';
import { SubscriptionDetailPanel } from './SubscriptionDetailPanel';

const HEALTH_PILL: Record<HealthStatus, { label: string; tone: 'ok' | 'warn' | 'crit' | 'neutral' | 'info' }> = {
  [HealthStatus.Green]: { label: 'Green', tone: 'ok' },
  [HealthStatus.Amber]: { label: 'Amber', tone: 'warn' },
  [HealthStatus.Red]: { label: 'Red', tone: 'crit' },
  [HealthStatus.Stalled]: { label: 'Stalled', tone: 'neutral' },
};

const LAST_STATUS_PILL: Record<NotificationStatus, { label: string; tone: 'ok' | 'warn' | 'crit' | 'neutral' | 'info' }> = {
  [NotificationStatus.Created]: { label: 'Created', tone: 'info' },
  [NotificationStatus.NotificationSent]: { label: 'Sent', tone: 'ok' },
  [NotificationStatus.NotificationSilenced]: { label: 'Silenced', tone: 'neutral' },
  [NotificationStatus.NoResults]: { label: 'No results', tone: 'ok' },
  [NotificationStatus.Timeout]: { label: 'Timeout', tone: 'crit' },
  [NotificationStatus.BelowThreshold]: { label: 'Below', tone: 'neutral' },
  [NotificationStatus.Failed]: { label: 'Failed', tone: 'crit' },
};

const HEALTH_OPTIONS: SegOption<string>[] = [
  { value: 'all', label: 'All' },
  { value: String(HealthStatus.Green), label: 'Green' },
  { value: String(HealthStatus.Amber), label: 'Amber' },
  { value: String(HealthStatus.Red), label: 'Red' },
  { value: String(HealthStatus.Stalled), label: 'Stalled' },
];

const TIME_RANGE_OPTIONS: SegOption<string>[] = [
  { value: '1', label: '24h' },
  { value: '7', label: '7d' },
  { value: '30', label: '30d' },
  { value: '90', label: '90d' },
];

function Sparkline({ points }: { points: AnomalySparklinePoint[] }) {
  if (points.length === 0) {
    return <span className="text-text-subtle text-2xs">—</span>;
  }
  const max = Math.max(...points.map(p => p.anomalyCount), 1);
  const width = 80;
  const height = 20;
  const step = points.length === 1 ? width : width / (points.length - 1);
  const path = points
    .map((p, i) => {
      const x = i * step;
      const y = height - (p.anomalyCount / max) * height;
      return `${i === 0 ? 'M' : 'L'} ${x.toFixed(1)} ${y.toFixed(1)}`;
    })
    .join(' ');
  const total = points.reduce((s, p) => s + p.anomalyCount, 0);
  return (
    <span className="inline-flex items-center gap-1.5" title={`${total} anomalies in window`}>
      <svg width={width} height={height} aria-hidden className="text-warn">
        <path d={path} fill="none" stroke="currentColor" strokeWidth={1.5} />
      </svg>
      <span className="text-2xs text-text-muted tabular-nums">{total}</span>
    </span>
  );
}

export default function ControlTowerPage() {
  // Filters, sort and page live in the URL (usePagedList); the statistics tiles use the same filters.
  const list = useControlTowerList();
  const { filters, setFilter } = list;
  const statistics = useControlTowerStatistics(filters);
  const [searchInput, setSearchInput] = useState(filters.searchKeyword);
  // Store the id, not a row snapshot — rows go stale across the 30s refetch.
  const [selectedSubscriptionId, setSelectedSubscriptionId] = useState<number | null>(null);

  const folderId = filters.folderId === '' ? undefined : Number(filters.folderId);
  const healthFilter = filters.healthStatus;
  const onlyOpenTasks = filters.openTasks === 'true';
  const timeRange = filters.timeRangeDays;
  const setFolderId = (value: number | undefined) => setFilter('folderId', value == null ? '' : String(value));
  const setHealthFilter = (value: string) => setFilter('healthStatus', value);
  const setOnlyOpenTasks = (value: boolean) => setFilter('openTasks', value ? 'true' : '');
  const setTimeRange = (value: string) => setFilter('timeRangeDays', value);

  const { data, isLoading, isError, error } = list;
  const isFetching = list.isFetching || statistics.isFetching;
  const refetch = () => {
    void list.refetch();
    void statistics.refetch();
  };
  const { data: folders } = useQueryFolders();

  const stats = statistics.data;
  const entries = list.items;

  const selectedRow =
    selectedSubscriptionId == null
      ? null
      : entries.find(r => r.subscriptionId === selectedSubscriptionId) ?? null;

  // Close the panel when the selected row drops out of the refetched data.
  useEffect(() => {
    if (selectedSubscriptionId == null || data == null) {
      return;
    }
    if (!data.items.some(r => r.subscriptionId === selectedSubscriptionId)) {
      setSelectedSubscriptionId(null);
    }
  }, [data, selectedSubscriptionId]);

  function applySearch() {
    setFilter('searchKeyword', searchInput.trim());
  }

  function clearFilters() {
    setSearchInput('');
    list.resetFilters();
  }

  const columns: Column<ControlTowerSubscriptionHealthData>[] = [
    {
      key: 'health',
      header: '',
      render: r => {
        const map = HEALTH_PILL[r.healthStatus] ?? { label: '?', tone: 'neutral' as const };
        return <Pill tone={map.tone}>{map.label}</Pill>;
      },
    },
    {
      key: 'name',
      header: 'Subscription',
      sortKey: 'queryName',
      render: r => (
        <div className="min-w-0">
          <div className="font-semibold text-text truncate">{r.queryName}</div>
          <div className="flex items-center gap-2 mt-0.5 text-xs text-text-muted">
            {r.folderPath && <span className="truncate">{r.folderPath}</span>}
            {r.aiActorName && <Pill tone="info">AI · {r.aiActorName}</Pill>}
            {r.hasAnomalyDetection && <Pill tone="info">Anomaly</Pill>}
          </div>
        </div>
      ),
    },
    {
      key: 'success',
      header: 'Success',
      sortKey: 'successRate',
      render: r =>
        r.totalExecutions === 0 ? (
          <span className="text-text-muted">—</span>
        ) : (
          <span className="tabular-nums">{formatPercentage(r.successRate, 1)}</span>
        ),
    },
    {
      key: 'execs',
      header: 'Runs',
      sortKey: 'totalExecutions',
      render: r => (
        <span className="tabular-nums">
          <span className="text-text">{formatNumber(r.totalExecutions)}</span>
          {r.failedExecutions > 0 && (
            <span className="text-crit ml-1">({r.failedExecutions} failed)</span>
          )}
        </span>
      ),
    },
    {
      key: 'tasks',
      header: 'Tasks',
      sortKey: 'unresolvedTaskCount',
      render: r =>
        r.unresolvedTaskCount > 0 ? (
          <Pill tone="warn">{r.unresolvedTaskCount} open</Pill>
        ) : (
          <span className="text-text-muted tabular-nums">{r.totalTaskCount}</span>
        ),
    },
    {
      key: 'anomalies',
      header: 'Anomalies',
      render: r => <Sparkline points={r.anomalySparkline} />,
    },
    {
      key: 'lastExec',
      header: 'Last run',
      render: r => {
        if (!r.lastExecutionTime) {
          return <span className="text-text-muted">never</span>;
        }
        const statusMap =
          r.lastExecutionStatus != null
            ? LAST_STATUS_PILL[r.lastExecutionStatus]
            : null;
        return (
          <div className="flex items-center gap-1.5 text-xs">
            {statusMap && <Pill tone={statusMap.tone}>{statusMap.label}</Pill>}
            <span title={String(r.lastExecutionTime)}>
              {formatRelativeTime(r.lastExecutionTime)}
            </span>
          </div>
        );
      },
    },
  ];

  const gridTemplate = '0.7fr 2.4fr 0.8fr 1fr 0.9fr 1.1fr 1.4fr';

  return (
    <div className="flex flex-col gap-4 p-7">
      <PageHeader
        variant="pulse"
        eyebrow="Operations"
        prefix="Control"
        emphasis="tower"
        sub={
          <span className="text-text-muted">
            Real-time health across subscriptions · auto-refreshing every 30s
          </span>
        }
        actions={
          <Button
            icon={<RefreshCw className={isFetching ? 'animate-spin' : undefined} />}
            type="button"
            onClick={() => refetch()}
            disabled={isFetching}
          >
            Refresh
          </Button>
        }
      />

      {isError && (
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load Control Tower"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      )}

      {!isError && (
        <>
          <KPIGrid>
            <KPI
              dot="brand"
              label="Total"
              value={formatNumber(stats?.totalSubscriptions ?? 0)}
              sub="subscriptions"
            />
            <KPI
              dot="ok"
              label="Healthy"
              value={formatNumber(stats?.healthySubscriptions ?? 0)}
              sub={`${formatPercentage(stats?.overallSuccessRate ?? 100, 0)} success`}
            />
            <KPI
              dot="warn"
              label="Warning"
              value={formatNumber(stats?.warningSubscriptions ?? 0)}
              sub="amber"
            />
            <KPI
              dot="crit"
              label="Critical"
              value={formatNumber(stats?.criticalSubscriptions ?? 0)}
              sub="red"
            />
            <KPI
              dot="info"
              label="Stalled"
              value={formatNumber(stats?.stalledSubscriptions ?? 0)}
              sub="no runs"
            />
            <KPI
              dot="brand"
              label="Open tasks"
              value={formatNumber(stats?.totalUnresolvedTasks ?? 0)}
              sub={`${formatNumber(stats?.totalAnomalies30Days ?? 0)} anomalies`}
            />
          </KPIGrid>

          <Card>
            <div className="p-3 border-b border-border flex flex-wrap items-center gap-2">
              <div className="relative">
                <Search
                  size={14}
                  className="absolute left-2 top-1/2 -translate-y-1/2 text-text-subtle pointer-events-none"
                />
                <Input
                  value={searchInput}
                  onChange={e => setSearchInput(e.target.value)}
                  onKeyDown={e => {
                    if (e.key === 'Enter') applySearch();
                  }}
                  onBlur={applySearch}
                  placeholder="Search subscription"
                  className="pl-7 w-56"
                />
              </div>
              <Select
                value={folderId == null ? '' : String(folderId)}
                onChange={e =>
                  setFolderId(e.target.value === '' ? undefined : Number(e.target.value))
                }
                className="w-48"
              >
                <option value="">All folders</option>
                {folders?.map(f => (
                  <option key={f.id} value={f.id}>
                    {f.path ?? f.name}
                  </option>
                ))}
              </Select>
              <Seg
                options={HEALTH_OPTIONS}
                value={healthFilter}
                onChange={v => setHealthFilter(v)}
              />
              <Seg
                options={TIME_RANGE_OPTIONS}
                value={timeRange}
                onChange={v => setTimeRange(v)}
              />
              <label className="inline-flex items-center gap-1.5 text-xs text-text-muted cursor-pointer select-none ml-1">
                <input
                  type="checkbox"
                  checked={onlyOpenTasks}
                  onChange={e => setOnlyOpenTasks(e.target.checked)}
                  className="accent-brand-500"
                />
                <ListChecks size={13} /> open tasks only
              </label>
              <div className="ml-auto flex items-center gap-2 text-2xs text-text-subtle">
                {data && (
                  <>
                    <Activity size={12} /> {formatNumber(data.totalCount)} match
                    {data.totalCount === 1 ? '' : 'es'}
                  </>
                )}
                <button
                  type="button"
                  onClick={clearFilters}
                  className="ml-2 text-text-muted hover:text-text"
                >
                  Clear
                </button>
              </div>
            </div>
            <DataTable
              columns={columns}
              rows={entries}
              {...list.tableProps}
              rowKey={r => r.subscriptionId}
              gridTemplate={gridTemplate}
              onRowClick={r => setSelectedSubscriptionId(r.subscriptionId)}
              empty={
                <EmptyState
                  icon={<Sparkles />}
                  title={isLoading ? 'Loading subscription health…' : 'No matching subscriptions'}
                  description={
                    isLoading
                      ? ''
                      : 'Try a wider time range, clear filters, or create a subscription to start monitoring.'
                  }
                />
              }
            />
          </Card>
        </>
      )}

      {selectedRow && (
        <SubscriptionDetailPanel
          row={selectedRow}
          timeRangeDays={Number(timeRange)}
          onClose={() => setSelectedSubscriptionId(null)}
        />
      )}
    </div>
  );
}
