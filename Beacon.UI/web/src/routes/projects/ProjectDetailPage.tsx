import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { AlertTriangle, BookOpen, Bot, Database, Folder, GitBranch, Key, Layers } from 'lucide-react';
import { Button, Card, CardBody, KPI, KPIGrid, PageHeader } from '@/components/beacon';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { Tabs, type TabDef } from '@/components/Tabs';
import { formatNumber, formatRelativeTime, formatDateTime } from '@/lib/format';
import {
  useProjectDetailQuery,
  useProjectDocumentationQuery,
  type ProjectRepositoryEntry,
  type ProjectDataSourceEntry,
  type ProjectDocSectionEntry,
} from './queries';
import { SetRepositoryTokenDialog } from './SetRepositoryTokenDialog';
import { EditDocumentationSectionDialog } from './EditDocumentationSectionDialog';
import { DocSectionContent } from './DocSectionContent';

type TabKey = 'overview' | 'repositories' | 'documentation' | 'aiActors';

const SOURCE_COLUMNS: Column<ProjectDataSourceEntry>[] = [
  { key: 'name', header: 'Name', render: r => r.name },
  { key: 'type', header: 'Type', render: r => <span className="text-text-muted mono">{r.type}</span> },
  { key: 'tables', header: 'Tables', render: r => formatNumber(r.tableCount) },
];

export default function ProjectDetailPage() {
  const { id } = useParams();
  const projectId = id ? Number.parseInt(id, 10) : Number.NaN;
  const { data, isLoading, isError, error } = useProjectDetailQuery(projectId);
  const [tab, setTab] = useState<TabKey>('overview');

  if (Number.isNaN(projectId)) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <PageHeader eyebrow="Project" emphasis="Project" />
        <EmptyState icon={<AlertTriangle />} title="Invalid project id" description={String(id)} />
      </div>
    );
  }

  if (isLoading) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <PageHeader eyebrow="Project" emphasis="Project" sub={<span className="text-text-muted">Loading…</span>} />
      </div>
    );
  }

  if (isError) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <PageHeader eyebrow="Project" emphasis="Project" />
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load project"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      </div>
    );
  }

  const project = data?.project;
  if (!project) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <PageHeader
          eyebrow="Project"
          emphasis={`Project #${projectId}`}
          sub={<Link to="/projects" className="text-text-muted">← back to projects</Link>}
        />
        <EmptyState icon={<Folder />} title="Project not found" />
      </div>
    );
  }

  const tabs: TabDef<TabKey>[] = [
    { key: 'overview', label: <><Layers size={13} /> Overview</> },
    { key: 'repositories', label: <><GitBranch size={13} /> Repositories</>, count: project.repositories.length },
    { key: 'documentation', label: <><BookOpen size={13} /> Documentation</> },
    { key: 'aiActors', label: <><Bot size={13} /> AI Actors</> },
  ];

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        eyebrow="Project"
        emphasis={project.name}
        sub={
          <span className="text-text-muted">
            <Link to="/projects" className="text-text-muted">Projects</Link>
            <span className="mx-1.5">/</span>
            #{project.id}
          </span>
        }
      />

      {project.description && (
        <Card>
          <CardBody>{project.description}</CardBody>
        </Card>
      )}

      <KPIGrid>
        <KPI dot="brand" label="Data sources" value={formatNumber(project.dataSources.length)} sub="connected" />
        <KPI dot="info" label="Tables" value={formatNumber(project.totalTables)} sub="across sources" />
        <KPI dot="ok" label="Repositories" value={formatNumber(project.repositories.length)} sub="indexed" />
        <KPI dot="warn" label="Code references" value={formatNumber(project.codeReferenceCount)} sub="found" />
      </KPIGrid>

      <Card>
        <Tabs tabs={tabs} active={tab} onChange={setTab} />

        {tab === 'overview' && (
          <CardBody>
            <h3 className="m-0 mb-2 text-sm font-semibold text-text">Data sources</h3>
            <DataTable
              columns={SOURCE_COLUMNS}
              rows={project.dataSources}
              // ProjectDataSourceEntry carries no id on the wire — name+index
              // keeps keys unique even if two sources share a name.
              rowKey={(r, idx) => `${r.name}-${idx}`}
              gridTemplate="2fr 1fr 0.6fr"
              empty={
                <EmptyState
                  icon={<Database />}
                  title="No data sources"
                  description="Connect a data source to this project to get started."
                />
              }
            />
          </CardBody>
        )}

        {tab === 'repositories' && <RepositoriesTab repositories={project.repositories} />}
        {tab === 'documentation' && <DocumentationTab projectId={project.id} />}
        {tab === 'aiActors' && <AiActorsTab projectId={project.id} dataSources={project.dataSources} />}
      </Card>
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
          <div className="font-semibold text-text">{r.name}</div>
          <div className="text-text-muted text-xs mt-0.5">{r.url}</div>
        </div>
      ),
    },
    { key: 'status', header: 'Status', render: r => <span className="text-text-muted">{r.scanStatus ?? '—'}</span> },
    { key: 'refs', header: 'References', render: r => formatNumber(r.referenceCount) },
    {
      key: 'lastScan',
      header: 'Last scan',
      render: r =>
        r.lastScanAt
          ? <span title={String(r.lastScanAt)}>{formatRelativeTime(r.lastScanAt)}</span>
          : <span className="text-text-muted">—</span>,
    },
    {
      key: 'actions',
      header: '',
      render: r => (
        <Button
          variant="ghost"
          onClick={e => { e.stopPropagation(); setTokenTarget({ id: r.id, name: r.name }); }}
          title="Set access token"
          aria-label={`Set token for ${r.name}`}
          icon={<Key />}
        />
      ),
    },
  ];

  return (
    <>
      <CardBody flush>
        <DataTable
          columns={columns}
          rows={repositories}
          rowKey={r => r.id}
          gridTemplate="2.4fr 0.8fr 0.8fr 1.1fr 60px"
          empty={
            <EmptyState
              icon={<GitBranch />}
              title="No repositories"
              description="Link a GitHub repository to surface code references."
            />
          }
        />
      </CardBody>
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
    return <CardBody><span className="text-text-muted">Loading documentation…</span></CardBody>;
  }

  if (isError) {
    return (
      <CardBody>
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load documentation"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      </CardBody>
    );
  }

  const latest = data?.latest;
  if (!latest) {
    return (
      <CardBody>
        <EmptyState
          icon={<BookOpen />}
          title="No documentation generated yet"
          description="Documentation will appear here once generated for this project."
        />
      </CardBody>
    );
  }

  const sections = [...latest.sections].sort((a, b) => a.sortOrder - b.sortOrder);

  return (
    <>
      <CardBody>
        <div className="text-text-muted text-xs mb-4">
          Generated <strong className="text-text">{formatRelativeTime(latest.generatedAt)}</strong>
          {' '}by <span className="mono">{latest.generatedByModel}</span>
          {' · '}
          {formatNumber(latest.tablesAnalyzed)} tables · {formatNumber(latest.codeReferencesAnalyzed)} code refs
          {' · '}
          {formatNumber(latest.inputTokens + latest.outputTokens)} tokens
          {' · '}
          {formatDateTime(latest.generatedAt)}
        </div>

        <div className="flex flex-col gap-6">
          {sections.map(section => (
            <section key={section.id}>
              <header className="flex items-center justify-between mb-2">
                <h3 className="m-0 text-sm font-semibold text-text">{section.title}</h3>
                <Button
                  variant="ghost"
                  onClick={() => setEditing(section)}
                  aria-label={`Edit ${section.title}`}
                >
                  Edit
                </Button>
              </header>
              <DocSectionContent content={section.content} />
            </section>
          ))}
        </div>
      </CardBody>

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

function AiActorsTab({ projectId, dataSources }: { projectId: number; dataSources: ProjectDataSourceEntry[] }) {
  if (dataSources.length === 0) {
    return (
      <CardBody>
        <EmptyState
          icon={<Bot />}
          title="No data sources"
          description="AI actors are scoped to a data source. Connect a data source to this project first."
        />
      </CardBody>
    );
  }

  return (
    <CardBody>
      <div className="flex items-center justify-between mb-3">
        <div className="text-text-muted text-xs">
          AI actors are scoped per data source. View all actors for this project below.
        </div>
        <Link to={`/ai-actors?projectId=${projectId}`}>
          <Button variant="primary" icon={<Bot />}>View AI actors</Button>
        </Link>
      </div>
      <div className="flex flex-col gap-2">
        {dataSources.map(ds => (
          <div
            key={ds.name}
            className="bg-surface border border-border rounded-md shadow-sm p-3 flex items-center justify-between"
          >
            <div>
              <div className="font-semibold">{ds.name}</div>
              <div className="text-text-muted mono text-xs">{ds.type}</div>
            </div>
          </div>
        ))}
      </div>
    </CardBody>
  );
}
