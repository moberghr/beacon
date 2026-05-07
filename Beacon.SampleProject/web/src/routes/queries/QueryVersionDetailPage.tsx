import { Link, useParams } from 'react-router-dom';
import { Icon } from '@/components/Icon';
import { PageHeader } from '@/components/layout/PageHeader';
import { EmptyState } from '@/components/data/EmptyState';

/**
 * Placeholder for the version-detail viewer. Real diff/preview ships in Batch 5
 * alongside the QueryEditor port.
 */
export default function QueryVersionDetailPage() {
  const { id, versionId } = useParams();

  return (
    <div className="page">
      <PageHeader
        title="Query version detail"
        sub={
          <span className="muted">
            <Link to={`/app/queries/${id}/versions`} className="muted">← back to versions</Link>
            <span style={{ margin: '0 6px' }}>·</span>
            v{versionId}
          </span>
        }
      />
      <EmptyState
        icon={<Icon.Layers size={20} />}
        title="Version detail ships in Batch 5"
        description="The full diff viewer + step preview lands alongside the QueryEditor port."
      />
    </div>
  );
}
