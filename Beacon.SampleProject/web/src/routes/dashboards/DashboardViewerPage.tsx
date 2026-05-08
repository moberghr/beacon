import { Link, useParams } from 'react-router-dom';
import { Icon } from '@/components/Icon';
import { PageHeader } from '@/components/layout/PageHeader';
import { EmptyState } from '@/components/data/EmptyState';
import { formatDateTime } from '@/lib/format';
import type { DashboardWidgetData } from '@/api/generated/beacon-api';
import { useDashboardQuery, WIDGET_TYPE, WIDGET_TYPE_LABEL } from './queries';

export default function DashboardViewerPage() {
  const params = useParams<{ id: string }>();
  const id = params.id ? Number(params.id) : undefined;
  const { data, isLoading, isError, error } = useDashboardQuery(id);

  return (
    <div className="page">
      <PageHeader
        title={data?.name ?? (isLoading ? 'Loading…' : 'Dashboard')}
        sub={
          <span className="muted">
            {data?.description ?? ''} {data?.createdTime && <>· created {formatDateTime(data.createdTime)}</>}
          </span>
        }
        actions={
          <div style={{ display: 'flex', gap: 8 }}>
            <Link className="btn" to="/dashboards">
              <Icon.ArrowsLR size={14} className="btn__icon" />
              All dashboards
            </Link>
            {id !== undefined && (
              <Link className="btn btn--primary" to={`/dashboards/${id}/edit`}>
                Edit
              </Link>
            )}
          </div>
        }
      />

      {isError && (
        <EmptyState
          icon={<Icon.Alert size={20} />}
          title="Failed to load dashboard"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      )}

      {!isError && !isLoading && (data?.widgets?.length ?? 0) === 0 && (
        <EmptyState
          icon={<Icon.Grid size={20} />}
          title="No widgets yet"
          description="Add widgets in the dashboard builder."
        />
      )}

      {!isError && (data?.widgets?.length ?? 0) > 0 && (
        <div
          style={{
            display: 'grid',
            gridTemplateColumns: 'repeat(auto-fill, minmax(280px, 1fr))',
            gap: 16,
          }}
        >
          {data!.widgets!.map(w => <WidgetCard key={w.id} widget={w} />)}
        </div>
      )}
    </div>
  );
}

function WidgetCard({ widget }: { widget: DashboardWidgetData }) {
  const label = WIDGET_TYPE_LABEL[widget.widgetType ?? 0] ?? 'Widget';
  return (
    <div className="card" style={{ padding: 16, minHeight: 160 }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 8 }}>
        <strong style={{ color: 'var(--text)' }}>{widget.title || 'Untitled'}</strong>
        <span className="pill">{label}</span>
      </div>
      <div className="muted" style={{ fontSize: 12, marginBottom: 12 }}>
        Position {widget.positionX ?? 0},{widget.positionY ?? 0} · {widget.width ?? 0}×{widget.height ?? 0}
      </div>
      <WidgetBody widget={widget} />
    </div>
  );
}

function WidgetBody({ widget }: { widget: DashboardWidgetData }) {
  const type = widget.widgetType ?? 0;
  const cfg = parseConfig(widget.configurationJson);

  if (type === WIDGET_TYPE.KpiCard) {
    return (
      <div style={{ display: 'grid', placeItems: 'center', minHeight: 80 }}>
        <div style={{ fontSize: 28, fontWeight: 700, color: 'var(--text)' }}>
          {cfg?.value !== undefined && cfg?.value !== null ? String(cfg.value) : '—'}
        </div>
        {cfg?.unit !== undefined && cfg?.unit !== null && <div className="muted">{String(cfg.unit)}</div>}
      </div>
    );
  }

  if (type === WIDGET_TYPE.Table || type === WIDGET_TYPE.Chart || type === WIDGET_TYPE.Gauge || type === WIDGET_TYPE.Mermaid) {
    return (
      <div className="muted" style={{ fontSize: 12 }}>
        Live rendering deferred — open in the Blazor viewer to see this widget. Configuration on file.
      </div>
    );
  }

  return <div className="muted">Unknown widget type {type}.</div>;
}

function parseConfig(json?: string | null): Record<string, unknown> | null {
  if (!json) return null;
  try {
    return JSON.parse(json);
  } catch {
    return null;
  }
}
