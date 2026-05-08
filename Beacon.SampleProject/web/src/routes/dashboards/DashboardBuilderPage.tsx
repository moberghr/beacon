import { Link, useParams } from 'react-router-dom';
import { Icon } from '@/components/Icon';
import { PageHeader } from '@/components/layout/PageHeader';
import { EmptyState } from '@/components/data/EmptyState';
import { useDashboardQuery } from './queries';

export default function DashboardBuilderPage() {
  const params = useParams<{ id: string }>();
  const id = params.id ? Number(params.id) : undefined;
  const { data } = useDashboardQuery(id);

  return (
    <div className="page">
      <PageHeader
        title={data?.name ? `Edit · ${data.name}` : 'Edit dashboard'}
        sub={<span className="muted">Widget builder is migrating to React</span>}
        actions={
          <Link className="btn" to={id !== undefined ? `/dashboards/${id}` : '/dashboards'}>
            <Icon.ArrowsLR size={14} className="btn__icon" />
            Back
          </Link>
        }
      />

      <EmptyState
        icon={<Icon.Wand size={20} />}
        title="Builder coming soon"
        description="The drag-and-drop dashboard builder is part of a follow-up batch. The current widgets are still readable in the viewer; widget editing remains in the legacy admin tooling for now."
      />

      {data && (
        <div className="card" style={{ padding: 16, marginTop: 16 }}>
          <div style={{ marginBottom: 12 }}>
            <strong>Widgets on this dashboard ({data.widgets?.length ?? 0})</strong>
          </div>
          <ul style={{ listStyle: 'none', padding: 0, margin: 0 }}>
            {(data.widgets ?? []).map(w => (
              <li key={w.id} style={{ padding: '8px 0', borderBottom: '1px solid var(--border)' }}>
                <strong style={{ color: 'var(--text)' }}>{w.title || 'Untitled'}</strong>
                <span className="muted" style={{ marginLeft: 8 }}>type {w.widgetType}</span>
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}
