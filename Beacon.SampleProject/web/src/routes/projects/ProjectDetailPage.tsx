import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import type {
  ProjectRepositoryEntry,
  ProjectDataSourceEntry,
  ProjectDocSectionEntry,
} from '@/api/generated/beacon-api';
import { Icon } from '@/components/Icon';
import { PageHeader } from '@/components/layout/PageHeader';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { Tabs, type TabDef } from '@/components/Tabs';
import { formatNumber, formatRelativeTime, formatDateTime } from '@/lib/format';
import {
  useProjectDetailQuery,
  useProjectDocumentationQuery,
} from './queries';
import { SetRepositoryTokenDialog } from './SetRepositoryTokenDialog';
import { EditDocumentationSectionDialog } from './EditDocumentationSectionDialog';
import { DocSectionContent } from './DocSectionContent';

type TabKey = 'overview' | 'repositories' | 'documentation' | 'aiActors';

const SOURCE_COLUMNS: Column<ProjectDataSourceEntry>[] = [
  { key: 'name', header: 'Name', render: r => r.name },
  { key: 'type', header: 'Type', render: r => <span className="muted mono">{r.type}</span> },
  { key: 'tables', header: 'Tables', render: r => formatNumber(r.tableCount) },
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

        {tab === 'repositories' && <RepositoriesTab repositories={project.repositories} />}
        {tab === 'documentation' && <DocumentationTab projectId={project.id} />}
        {tab === 'aiActors' && <AiActorsTab dataSources={project.dataSources} />}
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

// --- Repositories tab -------------------------------------------------

function RepositoriesTab({ repositories }: { repositories: ProjectRepositoryEntry[] }) {
  const [tokenTarget, setTokenTarget] = useState<{ id: number; name: string } | null>(null);

  const columns: Column<ProjectRepositoryEntry>[] = [
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
    { key: 'status', header: 'Status', render: r => <span className="muted">{r.scanStatus ?? '—'}</span> },
    { key: 'refs', header: 'References', render: r => formatNumber(r.referenceCount) },
    {
      key: 'lastScan',
      header: 'Last scan',
      render: r =>
        r.lastScanAt
          ? <span title={String(r.lastScanAt)}>{formatRelativeTime(r.lastScanAt)}</span>
          : <span className="muted">—</span>,
    },
    {
      key: 'actions',
      header: '',
      render: r => (
        <button
          type="button"
          className="btn btn--ghost"
          onClick={e => { e.stopPropagation(); setTokenTarget({ id: r.id, name: r.name }); }}
          title="Set access token"
          aria-label={`Set token for ${r.name}`}
        >
          <Icon.Key size={14} />
        </button>
      ),
    },
  ];

  return (
    <>
      <div className="card__body card__body--flush">
        <DataTable
          columns={columns}
          rows={repositories}
          rowKey={r => r.id}
          gridTemplate="2.4fr 0.8fr 0.8fr 1.1fr 60px"
          empty={
            <EmptyState
              icon={<Icon.Branch size={20} />}
              title="No repositories"
              description="Link a GitHub repository to surface code references."
            />
          }
        />
      </div>
      <SetRepositoryTokenDialog
        open={tokenTarget !== null}
        repository={tokenTarget}
        onClose={() => setTokenTarget(null)}
      />
    </>
  );
}

// --- Documentation tab ------------------------------------------------

function DocumentationTab({ projectId }: { projectId: number }) {
  const { data, isLoading, isError, error } = useProjectDocumentationQuery(projectId);
  const [editing, setEditing] = useState<ProjectDocSectionEntry | null>(null);

  if (isLoading) {
    return <div className="card__body"><span className="muted">Loading documentation…</span></div>;
  }

  if (isError) {
    return (
      <div className="card__body">
        <EmptyState
          icon={<Icon.Alert size={20} />}
          title="Failed to load documentation"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      </div>
    );
  }

  const latest = data?.latest;
  if (!latest) {
    return (
      <div className="card__body">
        <EmptyState
          icon={<Icon.Book size={20} />}
          title="No documentation generated yet"
          description="Generate project documentation from the existing Blazor view to populate this tab."
          action={
            <a className="btn btn--primary" href={`/beacon/projects/${projectId}`}>
              Open in Blazor
            </a>
          }
        />
      </div>
    );
  }

  const sections = [...latest.sections].sort((a, b) => a.sortOrder - b.sortOrder);

  return (
    <>
      <div className="card__body">
        <div className="muted" style={{ fontSize: 12, marginBottom: 16 }}>
          Generated <strong style={{ color: 'var(--text)' }}>{formatRelativeTime(latest.generatedAt)}</strong>
          {' '}by <span className="mono">{latest.generatedByModel}</span>
          {' · '}
          {formatNumber(latest.tablesAnalyzed)} tables · {formatNumber(latest.codeReferencesAnalyzed)} code refs
          {' · '}
          {formatNumber(latest.inputTokens + latest.outputTokens)} tokens
          {' · '}
          {formatDateTime(latest.generatedAt)}
        </div>

        <div style={{ display: 'flex', flexDirection: 'column', gap: 24 }}>
          {sections.map(section => (
            <section key={section.id}>
              <header style={{
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'space-between',
                marginBottom: 8,
              }}>
                <h3 className="card__title" style={{ margin: 0 }}>{section.title}</h3>
                <button
                  type="button"
                  className="btn btn--ghost"
                  onClick={() => setEditing(section)}
                  aria-label={`Edit ${section.title}`}
                >
                  Edit
                </button>
              </header>
              <DocSectionContent content={section.content} />
            </section>
          ))}
        </div>
      </div>

      <EditDocumentationSectionDialog
        open={editing !== null}
        section={editing}
        projectId={projectId}
        onClose={() => setEditing(null)}
      />
    </>
  );
}

// --- AI Actors tab ----------------------------------------------------

function AiActorsTab({ dataSources }: { dataSources: ProjectDataSourceEntry[] }) {
  // AI Actors are scoped to a data source. Until the AI actor pages migrate
  // (Batch 6), surface the project's data sources and link out to the Blazor
  // listing. The project detail entry doesn't expose data source ids, so the
  // Blazor link is the data sources index page.
  if (dataSources.length === 0) {
    return (
      <div className="card__body">
        <EmptyState
          icon={<Icon.Bot size={20} />}
          title="No data sources"
          description="AI actors are scoped to a data source. Connect a data source to this project first."
        />
      </div>
    );
  }

  return (
    <div className="card__body">
      <div className="muted" style={{ fontSize: 12, marginBottom: 12 }}>
        AI actors are scoped per data source. Open a data source to manage its actors.
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        {dataSources.map(ds => (
          <a
            key={ds.name}
            className="card"
            href="/beacon/data-sources"
            style={{
              padding: 12,
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'space-between',
              textDecoration: 'none',
              color: 'inherit',
            }}
          >
            <div>
              <div style={{ fontWeight: 600 }}>{ds.name}</div>
              <div className="muted mono" style={{ fontSize: 12 }}>{ds.type}</div>
            </div>
            <Icon.Chevron size={16} />
          </a>
        ))}
      </div>
    </div>
  );
}
