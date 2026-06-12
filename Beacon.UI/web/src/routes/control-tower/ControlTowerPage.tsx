import { useEffect, useMemo, useState } from 'react';
import {
  Activity,
  AlertTriangle,
  ArrowDown,
  ArrowUp,
  ChevronLeft,
  ChevronRight,
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
import { ControlTowerSortBy, HealthStatus, NotificationStatus } from '@/lib/enums';
import {
  type AnomalySparklinePoint,
  type ControlTowerFilters,
  type ControlTowerSubscriptionHealthData,
} from './api';
import {
  CONTROL_TOWER_PAGE_SIZE,
  useControlTowerQuery,
  useQueryFolders,
} from './queries';
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

type SortColumn = 'name' | 'successRate' | 'executions' | 'openTasks' | 'anomalies' | 'lastExec';
type SortDirection = 'asc' | 'desc';

const SORT_TO_API: Record<SortColumn, ControlTowerSortBy> = {
  name: ControlTowerSortBy.Name,
  successRate: ControlTowerSortBy.SuccessRate,
  executions: ControlTowerSortBy.Executions,
  openTasks: ControlTowerSortBy.OpenTasks,
  anomalies: ControlTowerSortBy.Anomalies,
  lastExec: ControlTowerSortBy.LastExecution,
};

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

function SortHeader({
  label,
  column,
  sort,
  setSort,
  align = 'left',
}: {
  label: string;
  column: SortColumn;
  sort: { column: SortColumn | null; direction: SortDirection };
  setSort: (s: { column: SortColumn; direction: SortDirection }) => void;
  align?: 'left' | 'right';
}) {
  const active = sort.column === column;
  return (
    <button
      type="button"
      onClick={() =>
        setSort({
          column,
          direction: active && sort.direction === 'asc' ? 'desc' : 'asc',
        })
      }
      className={`inline-flex items-center gap-1 hover:text-text ${
        align === 'right' ? 'flex-row-reverse w-full justify-start' : ''
      } ${active ? 'text-text' : 'text-text-muted'}`}
    >
      <span>{label}</span>
      {active && (sort.direction === 'asc' ? <ArrowUp size={11} /> : <ArrowDown size={11} />)}
    </button>
  );
}

export default function ControlTowerPage() {
  const [searchInput, setSearchInput] = useState('');
  const [searchKeyword, setSearchKeyword] = useState<string | undefined>();
  const [folderId, setFolderId] = useState<number | undefined>();
  const [healthFilter, setHealthFilter] = useState<string>('all');
  const [onlyOpenTasks, setOnlyOpenTasks] = useState(false);
  const [timeRange, setTimeRange] = useState<string>('30');
  // `column: null` means "no user-chosen sort" — the API then sorts worst-first,
  // which is the intended default for an operations view.
  const [sort, setSort] = useState<{ column: SortColumn | null; direction: SortDirection }>({
    column: null,
    direction: 'asc',
  });
  const [page, setPage] = useState(0);
  // Store the id, not a row snapshot — rows go stale across the 30s refetch.
  const [selectedSubscriptionId, setSelectedSubscriptionId] = useState<number | null>(null);

  const filters = useMemo<ControlTowerFilters>(
    () => ({
      searchKeyword,
      folderId,
      healthStatus: healthFilter === 'all' ? undefined : (Number(healthFilter) as HealthStatus),
      hasUnresolvedTasks: onlyOpenTasks ? true : undefined,
      timeRangeDays: Number(timeRange),
      sortBy: sort.column == null ? ControlTowerSortBy.WorstFirst : SORT_TO_API[sort.column],
    }),
    [searchKeyword, folderId, healthFilter, onlyOpenTasks, timeRange, sort.column],
  );

  // Filters define a new result set — page 0 is the only valid start.
  useEffect(() => {
    setPage(0);
  }, [filters]);

  const { data, isLoading, isFetching, isError, error, refetch } = useControlTowerQuery(filters, page);
  const { data: folders } = useQueryFolders();
  const totalPages = Math.max(1, Math.ceil((data?.totalCount ?? 0) / CONTROL_TOWER_PAGE_SIZE));

  const stats = data?.stats;
  const entries = data?.entries ?? [];

  const selectedRow =
    selectedSubscriptionId == null
      ? null
      : entries.find(r => r.subscriptionId === selectedSubscriptionId) ?? null;

  // Close the panel when the selected row drops out of the refetched data.
  useEffect(() => {
    if (selectedSubscriptionId == null || data == null) {
      return;
    }
    if (!data.entries.some(r => r.subscriptionId === selectedSubscriptionId)) {
      setSelectedSubscriptionId(null);
    }
  }, [data, selectedSubscriptionId]);

  // Client-side sort overlay for direction + tie-breaking. NOTE: this sorts
  // only within the currently fetched page — the API's `sortBy` drives the
  // server-side order that decides which rows land on each page.
  const sortedEntries = useMemo(() => {
    const column = sort.column;
    if (column == null) {
      // No user-chosen sort — keep the server's worst-first order.
      return entries;
    }
    const list = [...entries];
    const dir = sort.direction === 'asc' ? 1 : -1;
    list.sort((a, b) => {
      switch (column) {
        case 'name':
          return a.queryName.localeCompare(b.queryName) * dir;
        case 'successRate':
          return ((a.successRate || 0) - (b.successRate || 0)) * dir;
        case 'executions':
          return (a.totalExecutions - b.totalExecutions) * dir;
        case 'openTasks':
          return (a.unresolvedTaskCount - b.unresolvedTaskCount) * dir;
        case 'anomalies':
          return (a.anomalyCount30Days - b.anomalyCount30Days) * dir;
        case 'lastExec': {
          const at = a.lastExecutionTime ? new Date(a.lastExecutionTime).getTime() : 0;
          const bt = b.lastExecutionTime ? new Date(b.lastExecutionTime).getTime() : 0;
          return (at - bt) * dir;
        }
      }
    });
    return list;
  }, [entries, sort]);

  function applySearch() {
    setSearchKeyword(searchInput.trim() || undefined);
  }

  function clearFilters() {
    setSearchInput('');
    setSearchKeyword(undefined);
    setFolderId(undefined);
    setHealthFilter('all');
    setOnlyOpenTasks(false);
    setTimeRange('30');
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
      header: <SortHeader label="Subscription" column="name" sort={sort} setSort={setSort} />,
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
      header: <SortHeader label="Success" column="successRate" sort={sort} setSort={setSort} />,
      render: r =>
        r.totalExecutions === 0 ? (
          <span className="text-text-muted">—</span>
        ) : (
          <span className="tabular-nums">{formatPercentage(r.successRate, 1)}</span>
        ),
    },
    {
      key: 'execs',
      header: <SortHeader label="Runs" column="executions" sort={sort} setSort={setSort} />,
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
      header: <SortHeader label="Tasks" column="openTasks" sort={sort} setSort={setSort} />,
      render: r =>
        r.unresolvedTaskCount > 0 ? (
          <Pill tone="warn">{r.unresolvedTaskCount} open</Pill>
        ) : (
          <span className="text-text-muted tabular-nums">{r.totalTaskCount}</span>
        ),
    },
    {
      key: 'anomalies',
      header: <SortHeader label="Anomalies" column="anomalies" sort={sort} setSort={setSort} />,
      render: r => <Sparkline points={r.anomalySparkline} />,
    },
    {
      key: 'lastExec',
      header: <SortHeader label="Last run" column="lastExec" sort={sort} setSort={setSort} />,
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
              rows={sortedEntries}
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
            {totalPages > 1 && (
              <div className="flex items-center justify-end gap-2 p-3 border-t border-border text-xs text-text-muted">
                <Button
                  size="sm"
                  icon={<ChevronLeft />}
                  onClick={() => setPage(p => Math.max(0, p - 1))}
                  disabled={page <= 0}
                >
                  Prev
                </Button>
                <span className="tabular-nums">Page {page + 1} of {totalPages}</span>
                <Button
                  size="sm"
                  icon={<ChevronRight />}
                  onClick={() => setPage(p => Math.min(totalPages - 1, p + 1))}
                  disabled={page >= totalPages - 1}
                >
                  Next
                </Button>
              </div>
            )}
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
