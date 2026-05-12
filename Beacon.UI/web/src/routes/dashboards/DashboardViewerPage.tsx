import { Link, useParams } from 'react-router-dom';
import { AlertTriangle, ArrowLeftRight, LayoutGrid } from 'lucide-react';
import { PageHeader, Button, Card, Pill } from '@/components/beacon';
import { EmptyState } from '@/components/data/EmptyState';
import { MermaidDiagram } from '@/components/ui/MermaidDiagram';
import { formatDateTime } from '@/lib/format';
import type { DashboardWidgetData } from '@/api/generated/beacon-api';
import { useDashboardQuery, WIDGET_TYPE, WIDGET_TYPE_LABEL } from './queries';

export default function DashboardViewerPage() {
  const params = useParams<{ id: string }>();
  const id = params.id ? Number(params.id) : undefined;
  const { data, isLoading, isError, error } = useDashboardQuery(id);

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        variant="nodes"
        eyebrow="Dashboard"
        emphasis={data?.name ?? (isLoading ? 'Loading…' : 'Dashboard')}
        sub={
          <span className="text-text-muted">
            {data?.description ?? ''} {data?.createdTime && <>· created {formatDateTime(data.createdTime)}</>}
          </span>
        }
        actions={
          <div className="flex gap-2">
            <Link to="/dashboards">
              <Button icon={<ArrowLeftRight />}>All dashboards</Button>
            </Link>
            {id !== undefined && (
              <Link to={`/dashboards/${id}/edit`}>
                <Button variant="primary">Edit</Button>
              </Link>
            )}
          </div>
        }
      />

      {isError && (
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load dashboard"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      )}

      {!isError && !isLoading && (data?.widgets?.length ?? 0) === 0 && (
        <EmptyState
          icon={<LayoutGrid />}
          title="No widgets yet"
          description="Add widgets in the dashboard builder."
        />
      )}

      {!isError && (data?.widgets?.length ?? 0) > 0 && (
        <div className="grid gap-4 grid-cols-[repeat(auto-fill,minmax(280px,1fr))]">
          {data!.widgets!.map(w => <WidgetCard key={w.id} widget={w} />)}
        </div>
      )}
    </div>
  );
}

function WidgetCard({ widget }: { widget: DashboardWidgetData }) {
  const label = WIDGET_TYPE_LABEL[widget.widgetType ?? 0] ?? 'Widget';
  return (
    <Card className="p-4 min-h-[160px]">
      <div className="flex justify-between items-center mb-2">
        <strong className="text-text">{widget.title || 'Untitled'}</strong>
        <Pill>{label}</Pill>
      </div>
      <div className="text-text-muted text-xs mb-3">
        Position {widget.positionX ?? 0},{widget.positionY ?? 0} · {widget.width ?? 0}×{widget.height ?? 0}
      </div>
      <WidgetBody widget={widget} />
    </Card>
  );
}

function WidgetBody({ widget }: { widget: DashboardWidgetData }) {
  const type = widget.widgetType ?? 0;
  const cfg = parseConfig(widget.configurationJson);

  if (type === WIDGET_TYPE.KpiCard) {
    return (
      <div className="grid place-items-center min-h-[80px]">
        <div className="text-3xl font-bold text-text">
          {cfg?.value !== undefined && cfg?.value !== null ? String(cfg.value) : '—'}
        </div>
        {cfg?.unit !== undefined && cfg?.unit !== null && <div className="text-text-muted">{String(cfg.unit)}</div>}
      </div>
    );
  }

  if (type === WIDGET_TYPE.Table) {
    return <TableWidget cfg={cfg} />;
  }

  if (type === WIDGET_TYPE.Chart) {
    return <ChartWidget cfg={cfg} />;
  }

  if (type === WIDGET_TYPE.Gauge) {
    return <GaugeWidget cfg={cfg} />;
  }

  if (type === WIDGET_TYPE.Mermaid) {
    return <MermaidWidget cfg={cfg} />;
  }

  return <div className="text-text-muted">Unknown widget type {type}.</div>;
}

function TableWidget({ cfg }: { cfg: Record<string, unknown> | null }) {
  const rows = Array.isArray(cfg?.rows) ? (cfg!.rows as Record<string, unknown>[]) : [];
  const columns: string[] = Array.isArray(cfg?.columns)
    ? (cfg!.columns as string[])
    : rows.length > 0
      ? Object.keys(rows[0])
      : [];

  if (rows.length === 0) {
    return <EmptyState icon={<LayoutGrid />} title="No data" />;
  }

  return (
    <div className="overflow-x-auto max-h-[300px]">
      <table className="w-full text-sm">
        <thead>
          <tr className="text-left text-2xs font-semibold uppercase tracking-eyebrow text-text-muted border-b border-border">
            {columns.map(c => <th key={c} className="px-2 py-1.5">{c}</th>)}
          </tr>
        </thead>
        <tbody>
          {rows.map((row, idx) => (
            <tr key={idx} className="border-b border-border last:border-b-0">
              {columns.map(c => (
                <td key={c} className="px-2 py-1.5">{row[c] == null ? '—' : String(row[c])}</td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function ChartWidget({ cfg }: { cfg: Record<string, unknown> | null }) {
  const data = Array.isArray(cfg?.data) ? (cfg!.data as Record<string, unknown>[]) : [];
  const chartType = (cfg?.chartType as string | undefined) ?? 'bar';

  if (data.length === 0) {
    return <EmptyState icon={<LayoutGrid />} title="No chart data" />;
  }

  // Simple SVG bar chart — no third-party needed for a minimal representation
  const keys = data.length > 0 ? Object.keys(data[0]).filter(k => typeof (data[0] as Record<string, unknown>)[k] === 'number') : [];
  const labelKey = Object.keys(data[0]).find(k => !keys.includes(k)) ?? keys[0] ?? '';

  return (
    <div className="flex flex-col gap-1">
      <div className="text-text-muted text-[11px] mb-1">
        {chartType} chart · {data.length} data point{data.length === 1 ? '' : 's'}
        {keys.length > 0 && ` · series: ${keys.join(', ')}`}
      </div>
      <SimpleBarChart data={data} labelKey={labelKey} valueKeys={keys.slice(0, 3)} />
    </div>
  );
}

function SimpleBarChart({
  data,
  labelKey,
  valueKeys,
}: {
  data: Record<string, unknown>[];
  labelKey: string;
  valueKeys: string[];
}) {
  if (valueKeys.length === 0 || data.length === 0) {
    return <div className="text-text-muted text-xs">No numeric series found in data.</div>;
  }

  const primaryKey = valueKeys[0];
  const values = data.map(d => Number((d as Record<string, unknown>)[primaryKey] ?? 0));
  const maxVal = Math.max(...values, 1);

  return (
    <div className="flex flex-col gap-1 max-h-[180px] overflow-y-auto">
      {data.map((d, i) => {
        const label = String((d as Record<string, unknown>)[labelKey] ?? i);
        const val = values[i];
        const pct = (val / maxVal) * 100;
        return (
          <div key={i} className="flex items-center gap-2 text-xs">
            <div className="text-text-muted w-[60px] text-right shrink-0 truncate">
              {label}
            </div>
            <div className="flex-1 bg-border rounded-xs h-3">
              <div
                className="h-full bg-brand-600 rounded-xs"
                style={{ width: `${pct}%` }}
              />
            </div>
            <div className="w-10 text-right shrink-0">{val}</div>
          </div>
        );
      })}
    </div>
  );
}

function GaugeWidget({ cfg }: { cfg: Record<string, unknown> | null }) {
  const value = Number(cfg?.value ?? 0);
  const max = Number(cfg?.max ?? 100);
  const label = cfg?.label ? String(cfg.label) : undefined;
  const pct = Math.min(Math.max(value / Math.max(max, 1), 0), 1);

  // SVG arc gauge
  const r = 36;
  const cx = 50;
  const cy = 50;
  const startAngle = -150;
  const sweepAngle = 300;
  const endAngle = startAngle + sweepAngle * pct;

  const polarToXY = (angleDeg: number, radius: number) => {
    const rad = (angleDeg - 90) * (Math.PI / 180);
    return {
      x: cx + radius * Math.cos(rad),
      y: cy + radius * Math.sin(rad),
    };
  };

  const describeArc = (start: number, end: number) => {
    const s = polarToXY(start, r);
    const e = polarToXY(end, r);
    const largeArc = Math.abs(end - start) > 180 ? 1 : 0;
    return `M ${s.x} ${s.y} A ${r} ${r} 0 ${largeArc} 1 ${e.x} ${e.y}`;
  };

  return (
    <div className="flex flex-col items-center gap-1">
      <svg viewBox="0 0 100 80" className="w-[120px] h-[80px]">
        <path
          d={describeArc(startAngle, startAngle + sweepAngle)}
          fill="none"
          stroke="var(--border)"
          strokeWidth={8}
          strokeLinecap="round"
        />
        {pct > 0 && (
          <path
            d={describeArc(startAngle, endAngle)}
            fill="none"
            stroke="var(--brand-600)"
            strokeWidth={8}
            strokeLinecap="round"
          />
        )}
        <text x={cx} y={58} textAnchor="middle" fontSize={14} fontWeight={700} fill="var(--text)">
          {value}
        </text>
      </svg>
      {label && <div className="text-text-muted text-xs">{label}</div>}
      <div className="text-text-muted text-[11px]">
        {value} / {max} ({Math.round(pct * 100)}%)
      </div>
    </div>
  );
}

function MermaidWidget({ cfg }: { cfg: Record<string, unknown> | null }) {
  const code = cfg?.code ? String(cfg.code) : '';

  if (!code.trim()) {
    return <EmptyState icon={<LayoutGrid />} title="No diagram code" />;
  }

  return <MermaidDiagram chart={code} />;
}

function parseConfig(json?: string | null): Record<string, unknown> | null {
  if (!json) return null;
  try {
    return JSON.parse(json);
  } catch {
    return null;
  }
}
