import { Link, useParams } from 'react-router-dom';
import { PageHeader } from '@/components/layout/PageHeader';
import { EmptyState } from '@/components/data/EmptyState';
import { Icon } from '@/components/Icon';

/**
 * Placeholder. Real port lands in Phase 3 Batch 2.
 */
export default function ProjectDetailPage() {
  const { id } = useParams();

  return (
    <div className="page">
      <PageHeader
        title={`Project #${id}`}
        sub={<Link to="/app/projects" className="muted">← back to projects</Link>}
      />
      <EmptyState
        icon={<Icon.Layers size={20} />}
        title="Detail view ships in Batch 2"
        description="The full project page (overview, repositories, documentation, AI actors) is coming next."
      />
    </div>
  );
}
