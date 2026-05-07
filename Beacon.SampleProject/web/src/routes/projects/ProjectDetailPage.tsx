import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import type {
  ProjectRepositoryEntry,
  ProjectDataSourceEntry,
} from '@/api/generated/beacon-api';
import { Icon } from '@/components/Icon';
import { PageHeader } from '@/components/layout/PageHeader';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { Tabs, type TabDef } from '@/components/Tabs';
import { formatNumber, formatRelativeTime } from '@/lib/format';
import { useProjectDetailQuery } from './queries';

type TabKey = 'overview' | 'repositories' | 'documentation' | 'aiActors';

const SOURCE_COLUMNS: Column<ProjectDataSourceEntry>[] = [
  { key: 'name', header: 'Name', render: r => r.name },
  { key: 'type', header: 'Type', render: r => <span className="muted mono">{r.type}</span> },
  { key: 'tables', header: 'Tables', render: r => formatNumber(r.tableCount) },
];

const REPO_COLUMNS: Column<ProjectRepositoryEntry>[] = [
  {
    key: 'name',
    header: 'Repository',
    render: r => (
      <div>
        <div style={{ fontWeight: 600, color: 'var(--text)' }}>{r.name}</div>
        <div className="muted" style={{ fontSize: 12, marginTop: 2 }}>{r.url}</div>
      </div>
    ),
  },
  {
    key: 'status',
    header: 'Status',
    render: r => <span className="muted">{r.scanStatus ?? '—'}</span>,
  },
  {
    key: 'refs',
    header: 'References',
    render: r => formatNumber(r.referenceCount),
  },
  {
    key: 'lastScan',
    header: 'Last scan',
    render: r =>
      r.lastScanAt
        ? <span title={String(r.lastScanAt)}>{formatRelativeTime(r.lastScanAt)}</span>
        : <span className="muted">—</span>,
  },
];

export default function ProjectDetailPage() {
  const { id } = useParams();
  const projectId = id ? Number.parseInt(id, 10) : Number.NaN;
  const { data, isLoading, isError, error } = useProjectDetailQuery(projectId);
  const [tab, setTab] = useState<TabKey>('overview');

  if (Number.isNaN(projectId)) {
    return (
      <div className="page">
        <PageHeader title="Project" />
        <EmptyState icon={<Icon.Alert size={20} />} title="Invalid project id" description={String(id)} />
      </div>
    );
  }

  if (isLoading) {
    return (
      <div className="page">
        <PageHeader title="Project" sub={<span className="muted">Loading…</span>} />
      </div>
    );
  }

  if (isError) {
    return (
      <div className="page">
        <PageHeader title="Project" />
        <EmptyState
          icon={<Icon.Alert size={20} />}
          title="Failed to load project"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      </div>
    );
  }

  const project = data?.project;
  if (!project) {
    return (
      <div className="page">
        <PageHeader
          title={`Project #${projectId}`}
          sub={<Link to="/projects" className="muted">← back to projects</Link>}
        />
        <EmptyState icon={<Icon.Folder size={20} />} title="Project not found" />
      </div>
    );
  }

  const tabs: TabDef<TabKey>[] = [
    { key: 'overview', label: <><Icon.Layers size={13} /> Overview</> },
    { key: 'repositories', label: <><Icon.Branch size={13} /> Repositories</>, count: project.repositories.length },
    { key: 'documentation', label: <><Icon.Book size={13} /> Documentation</> },
    { key: 'aiActors', label: <><Icon.Bot size={13} /> AI Actors</> },
  ];

  return (
    <div className="page">
      <PageHeader
        title={project.name}
        sub={
          <span className="muted">
            <Link to="/projects" className="muted">Projects</Link>
            <span style={{ margin: '0 6px' }}>/</span>
            #{project.id}
          </span>
        }
      />

      {project.description && (
        <div className="card" style={{ marginBottom: 16 }}>
          <div className="card__body">{project.description}</div>
        </div>
      )}

      <div className="kpi-grid" style={{ marginBottom: 16 }}>
        <Kpi dot="brand" label="Data sources" value={formatNumber(project.dataSources.length)} sub="connected" />
        <Kpi dot="info" label="Tables" value={formatNumber(project.totalTables)} sub="across sources" />
        <Kpi dot="ok" label="Repositories" value={formatNumber(project.repositories.length)} sub="indexed" />
        <Kpi dot="warn" label="Code references" value={formatNumber(project.codeReferenceCount)} sub="found" />
      </div>

      <div className="card">
        <Tabs tabs={tabs} active={tab} onChange={setTab} />

        {tab === 'overview' && (
          <div className="card__body">
            <h3 className="card__title" style={{ margin: '0 0 8px' }}>Data sources</h3>
            <DataTable
              columns={SOURCE_COLUMNS}
              rows={project.dataSources}
              rowKey={r => r.name}
              gridTemplate="2fr 1fr 0.6fr"
              empty={
                <EmptyState
                  icon={<Icon.Database size={20} />}
                  title="No data sources"
                  description="Connect a data source to this project to get started."
                />
              }
            />
          </div>
        )}

        {tab === 'repositories' && (
          <div className="card__body card__body--flush">
            <DataTable
              columns={REPO_COLUMNS}
              rows={project.repositories}
              rowKey={r => r.id}
              gridTemplate="2.4fr 0.8fr 0.8fr 1.1fr"
              empty={
                <EmptyState
                  icon={<Icon.Branch size={20} />}
                  title="No repositories"
                  description="Link a GitHub repository to surface code references."
                />
              }
            />
          </div>
        )}

        {tab === 'documentation' && (
          <div className="card__body">
            <EmptyState
              icon={<Icon.Book size={20} />}
              title={project.hasDocumentation ? 'Documentation viewer ships in Batch 4' : 'No documentation yet'}
              description={
                project.hasDocumentation
                  ? 'The full documentation viewer (sections, instruct, export) lands in Batch 4.'
                  : 'Generate documentation from the Blazor view until Batch 4 ships.'
              }
            />
          </div>
        )}

        {tab === 'aiActors' && (
          <div className="card__body">
            <EmptyState
              icon={<Icon.Bot size={20} />}
              title="AI Actors view ships in Batch 4"
              description="Per-project AI actor configuration lands in Batch 4."
            />
          </div>
        )}
      </div>
    </div>
  );
}

function Kpi({ dot, label, value, sub }: { dot: string; label: string; value: string; sub: string }) {
  return (
    <div className="kpi">
      <div className="kpi__head">
        <span className={`kpi__dot kpi__dot--${dot}`}></span>
        <span className="kpi__label">{label}</span>
      </div>
      <div className="kpi__value">{value}</div>
      <div className="kpi__sub"><span className="muted">{sub}</span></div>
    </div>
  );
}
