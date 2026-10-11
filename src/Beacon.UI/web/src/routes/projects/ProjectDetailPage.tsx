import { Fragment, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { AlertTriangle, BookOpen, Bot, Database, Folder, GitBranch, Key, Layers, Link2, Lock, Shield, Unlink, Users } from 'lucide-react';
import {
  Banner,
  Button,
  Card,
  CardActions,
  CardBody,
  CardHead,
  CardSub,
  CardTitle,
  KPI,
  KPIGrid,
  PageHeader,
  Seg,
} from '@/components/beacon';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { Tabs, type TabDef } from '@/components/Tabs';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { useIsAdmin } from '@/auth/useAuth';
import { isNotFound, NOT_FOUND_DETAIL } from '@/lib/api';
import { ProjectMembershipManagement, ProjectRole } from '@/lib/enums';
import { formatNumber, formatRelativeTime, formatDateTime } from '@/lib/format';
import {
  PROJECT_ROLE_LABEL,
  useProjectDetailQuery,
  useProjectDocumentationQuery,
  useSetProjectAccess,
  useUnlinkProjectDataSource,
  type ProjectDetailEntry,
  type SharedSourceGuardrail,
  type SharedSourceProjectEntry,
  type ProjectRepositoryEntry,
  type ProjectDataSourceEntry,
  type ProjectDocSectionEntry,
} from './queries';
import { SetRepositoryTokenDialog } from './SetRepositoryTokenDialog';
import { EditDocumentationSectionDialog } from './EditDocumentationSectionDialog';
import { DocSectionContent } from './DocSectionContent';
import { LinkDataSourcesModal } from './LinkDataSourcesModal';
import { MembersTab } from './MembersTab';
import { RestrictedPill } from './RestrictedPill';

type TabKey = 'overview' | 'members' | 'repositories' | 'documentation' | 'aiActors';

const SOURCE_COLUMNS: Column<ProjectDataSourceEntry>[] = [
  { key: 'name', header: 'Name', render: r => r.name },
  { key: 'type', header: 'Type', render: r => <span className="text-text-muted mono">{r.type}</span> },
  { key: 'tables', header: 'Tables', render: r => formatNumber(r.tableCount) },
];

export default function ProjectDetailPage() {
  const { id } = useParams();
  const projectId = id ? Number.parseInt(id, 10) : Number.NaN;
  const { data, isLoading, isError, error } = useProjectDetailQuery(projectId);
  const isAdmin = useIsAdmin();
  const [tab, setTab] = useState<TabKey>('overview');
  const [linking, setLinking] = useState(false);

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

  if (isError && !isNotFound(error)) {
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

  // A project the caller may not read answers 404, like a missing one: both show the not-found state.
  const project = data?.project;
  if (!project) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <PageHeader
          eyebrow="Project"
          emphasis={`Project #${projectId}`}
          sub={<Link to="/projects" className="text-text-muted">← back to projects</Link>}
        />
        <EmptyState
          icon={<Folder />}
          title="Project not found"
          description={NOT_FOUND_DETAIL}
        />
      </div>
    );
  }

  // Linking needs the project-admin role (an Admin has it everywhere), whoever manages membership.
  const canLink = project.myRole === ProjectRole.ProjectAdmin;

  const tabs: TabDef<TabKey>[] = [
    { key: 'overview', label: <><Layers size={13} /> Overview</> },
    // Hidden unless the caller manages members and Beacon (not the host) manages membership.
    ...(project.canManageMembers ? [{ key: 'members' as const, label: <><Users size={13} /> Members</> }] : []),
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
          <span className="flex flex-wrap items-center gap-2 text-text-muted">
            <span>
              <Link to="/projects" className="text-text-muted">Projects</Link>
              <span className="mx-1.5">/</span>
              #{project.id}
            </span>
            {project.requiresMembership && <RestrictedPill />}
            {project.myRole != null && <span className="text-xs">Your role: {PROJECT_ROLE_LABEL[project.myRole]}</span>}
          </span>
        }
      />

      {isAdmin === true && <AccessCard project={project} />}

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
            <div className="flex items-center justify-between mb-2">
              <h3 className="m-0 text-sm font-semibold text-text">Data sources</h3>
              {canLink && (
                <Button size="sm" icon={<Link2 />} onClick={() => setLinking(true)}>
                  Link data sources
                </Button>
              )}
            </div>
            <DataTable
              columns={SOURCE_COLUMNS}
              rows={project.dataSources}
              rowKey={r => r.id}
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

        {tab === 'members' && project.canManageMembers && <MembersTab projectId={project.id} />}
        {tab === 'repositories' && <RepositoriesTab repositories={project.repositories} />}
        {tab === 'documentation' && <DocumentationTab projectId={project.id} />}
        {tab === 'aiActors' && <AiActorsTab projectId={project.id} dataSources={project.dataSources} />}
      </Card>

      {canLink && (
        <LinkDataSourcesModal
          open={linking}
          projectId={project.id}
          projectName={project.name}
          linkedDataSourceIds={project.dataSources.map(x => x.id)}
          onClose={() => setLinking(false)}
        />
      )}
    </div>
  );
}

// --- Access (Admin) ---------------------------------------------------

type AccessMode = 'open' | 'restricted';

const ACCESS_OPTIONS = [
  { value: 'open' as const, label: 'Open' },
  { value: 'restricted' as const, label: <><Lock size={12} className="inline" /> Restricted</> },
];

/** Why members are not managed here, when they are not. */
const MEMBERSHIP_NOTE: Partial<Record<ProjectMembershipManagement, string>> = {
  [ProjectMembershipManagement.Host]: ' Members are managed by the host application, not here.',
  [ProjectMembershipManagement.Unavailable]:
    ' Project membership is not available in this deployment, so projects cannot be restricted.',
};

/**
 * Admin only: an open project is used by everyone with a Beacon role; a restricted one only by its members and Admins.
 * Data sources another open project also links stay reachable through that project, and every other project linking
 * them keeps reaching them: the card lists those projects, with an unlink per shared data source. The server refuses
 * (409) to restrict a project while project membership is unavailable.
 */
function AccessCard({ project }: { project: ProjectDetailEntry }) {
  const setAccess = useSetProjectAccess(project.id);
  const [pending, setPending] = useState<AccessMode | null>(null);
  const current: AccessMode = project.requiresMembership ? 'restricted' : 'open';
  const membershipNote = MEMBERSHIP_NOTE[project.membershipManagement];
  const openlyReachable = project.dataSources.filter(x => project.openlyReachableDataSourceIds.includes(x.id));

  const onConfirm = async () => {
    if (pending == null) return;
    try {
      await setAccess.mutateAsync(pending === 'restricted');
      setPending(null);
    } catch {
      // Error toast already raised by the access mutation hook.
    }
  };

  return (
    <Card>
      <CardHead>
        <div>
          <CardTitle>Access</CardTitle>
          <CardSub>
            Open: everyone with a Beacon role can use this project. Restricted: only its members and Admins.
            {membershipNote}
          </CardSub>
        </div>
        <CardActions>
          <Seg<AccessMode>
            ariaLabel="Project access"
            options={ACCESS_OPTIONS}
            value={current}
            onChange={x => {
              if (x !== current) {
                setPending(x);
              }
            }}
          />
        </CardActions>
      </CardHead>
      {openlyReachable.length > 0 && (
        <CardBody>
          <Banner
            tone="warn"
            icon={<AlertTriangle />}
            title="Some data sources stay reachable through open projects"
            sub={
              <>
                {openlyReachable.map(x => x.name).join(', ')}{' '}
                {openlyReachable.length === 1 ? 'is' : 'are'} also linked to an open project, so restricting this
                project does not restrict {openlyReachable.length === 1 ? 'it' : 'them'}.
              </>
            }
          />
        </CardBody>
      )}

      {project.sharedSourceProjects.length > 0 && <SharedSourceProjects project={project} />}

      {project.sharedSourceGuardrails.length > 0 && <SharedSourceGuardrails project={project} />}

      <ConfirmDialog
        open={pending != null}
        title={pending === 'restricted' ? 'Restrict project' : 'Open project'}
        message={
          pending === 'restricted'
            ? <>Only members of <strong>{project.name}</strong> and Admins will be able to use it. Add members (a project admin first) before restricting.</>
            : <>Everyone with a Beacon role will be able to use <strong>{project.name}</strong> and its data sources.</>
        }
        confirmLabel={pending === 'restricted' ? 'Restrict' : 'Open'}
        destructive={pending === 'restricted'}
        busy={setAccess.isPending}
        onConfirm={onConfirm}
        onCancel={() => setPending(null)}
      />
    </Card>
  );
}

interface UnlinkTarget {
  projectId: number;
  projectName: string;
  dataSourceId: number;
  dataSourceName: string;
}

/**
 * The other projects that link this project's data sources, open or restricted, with their project admins: their
 * members keep reaching those sources whatever this project's access. An Admin can unlink a shared source from one of
 * them.
 */
function SharedSourceProjects({ project }: { project: ProjectDetailEntry }) {
  const unlink = useUnlinkProjectDataSource();
  const [target, setTarget] = useState<UnlinkTarget | null>(null);
  const sourceName = (id: number) => project.dataSources.find(x => x.id === id)?.name ?? `Data source #${id}`;

  const columns: Column<SharedSourceProjectEntry>[] = [
    {
      key: 'project',
      header: 'Project',
      render: r => (
        <span className="flex flex-wrap items-center gap-2">
          <Link to={`/projects/${r.projectId}`} className="font-semibold text-text">{r.name}</Link>
          {r.requiresMembership && <RestrictedPill />}
        </span>
      ),
    },
    {
      key: 'admins',
      header: 'Project admins',
      render: r =>
        r.projectAdminNames.length > 0
          ? <span>{r.projectAdminNames.join(', ')}</span>
          : <span className="text-text-muted">No project admin</span>,
    },
    {
      key: 'sources',
      header: 'Shared data sources',
      render: r => (
        <span className="flex flex-wrap items-center gap-1.5">
          {r.sharedDataSourceIds.map(id => (
            <span key={id} className="inline-flex items-center gap-1 rounded-sm border border-border px-1.5 py-0.5 text-xs">
              {sourceName(id)}
              <Button
                variant="ghost"
                size="sm"
                icon={<Unlink />}
                aria-label={`Unlink ${sourceName(id)} from ${r.name}`}
                title={`Unlink from ${r.name}`}
                onClick={() =>
                  setTarget({ projectId: r.projectId, projectName: r.name, dataSourceId: id, dataSourceName: sourceName(id) })
                }
              />
            </span>
          ))}
        </span>
      ),
    },
  ];

  const onConfirm = async () => {
    if (target == null) return;
    try {
      await unlink.mutateAsync({ projectId: target.projectId, dataSourceId: target.dataSourceId });
      setTarget(null);
    } catch {
      // Error toast already raised by the unlink mutation hook, which also reloads the links.
    }
  };

  return (
    <CardBody className="flex flex-col gap-2">
      <div>
        <h3 className="m-0 text-sm font-semibold text-text">Other projects using these data sources</h3>
        <p className="m-0 text-xs text-text-muted">
          Their members keep reaching the shared data sources, whether this project is open or restricted. Unlink a
          data source from a project to stop that.
        </p>
      </div>
      <DataTable
        columns={columns}
        rows={project.sharedSourceProjects}
        rowKey={r => r.projectId}
        gridTemplate="1.4fr 1.4fr 2fr"
        ariaLabel="Other projects using these data sources"
      />
      <ConfirmDialog
        open={target != null}
        title="Unlink data source"
        message={
          target
            ? <>Unlink <strong>{target.dataSourceName}</strong> from <strong>{target.projectName}</strong>? Members of {target.projectName} no longer reach it through that project; <strong>{project.name}</strong> keeps it linked.</>
            : ''
        }
        confirmLabel="Unlink"
        destructive
        busy={unlink.isPending}
        onConfirm={onConfirm}
        onCancel={() => setTarget(null)}
      />
    </CardBody>
  );
}

const GUARDRAIL_LABEL: Record<string, string> = {
  enforceReadOnly: 'Read-only',
  enablePiiDetection: 'PII detection',
  customPiiPatterns: 'Custom PII patterns',
  maxRowLimit: 'Row limit',
  statementTimeoutSeconds: 'Statement timeout',
  maxResultBytes: 'Result size limit',
  maxExplainCost: 'Plan cost limit',
  enableValueGrounding: 'Value grounding',
  valueGroundingMaxProbes: 'Value probes',
  enableSampleValueCollection: 'Sample value collection',
};

/** A guardrail's effective value as the server sends it (`true`, a number, `none`), worded for reading. */
function guardrailValue(guardrail: SharedSourceGuardrail): string {
  const { setting, value } = guardrail;
  if (value === 'true' || value === 'false') {
    return value === 'true' ? 'on' : 'off';
  }
  if (setting === 'maxExplainCost' && value === 'none') {
    return 'no limit';
  }
  const amount = Number(value);
  if (!Number.isFinite(amount)) {
    return value;
  }
  const count = amount.toLocaleString();
  switch (setting) {
    case 'maxRowLimit':
      return `${count} rows`;
    case 'statementTimeoutSeconds':
      return `${count} s`;
    case 'maxResultBytes':
      return `${count} bytes`;
    case 'customPiiPatterns':
      return `${count} pattern${amount === 1 ? '' : 's'}`;
    case 'valueGroundingMaxProbes':
      return `${count} probe${amount === 1 ? '' : 's'}`;
    default:
      return count;
  }
}

/**
 * Admin only: a data source other live projects link too is held to the strictest of the linking projects' MCP
 * settings, whichever project queries it. Per shared source, each setting's effective value and the project that sets it.
 */
function SharedSourceGuardrails({ project }: { project: ProjectDetailEntry }) {
  return (
    <CardBody className="flex flex-col gap-3">
      <div>
        <h3 className="m-0 text-sm font-semibold text-text">Guardrails of shared data sources</h3>
        <p className="m-0 text-xs text-text-muted">
          A data source several projects link is held to the strictest of their settings, whichever project queries it.
          Each value names the project that sets it.
        </p>
      </div>
      {project.sharedSourceGuardrails.map(source => (
        <section
          key={source.dataSourceId}
          aria-label={`Guardrails of ${source.dataSourceName}`}
          className="rounded-sm border border-border p-3"
        >
          <div className="mb-2 flex items-center gap-1.5 text-sm font-semibold text-text">
            <Shield size={13} className="text-text-muted" /> {source.dataSourceName}
          </div>
          <dl className="m-0 grid grid-cols-[minmax(140px,auto)_1fr] gap-x-3 gap-y-1 text-sm">
            {source.guardrails.map(guardrail => (
              <Fragment key={guardrail.setting}>
                <dt className="text-text-muted">{GUARDRAIL_LABEL[guardrail.setting] ?? guardrail.setting}</dt>
                <dd className="m-0">
                  <span className="mono">{guardrailValue(guardrail)}</span>
                  <span className="text-text-muted"> · set by </span>
                  {guardrail.projectId === project.id ? (
                    <span className="text-text-muted">this project</span>
                  ) : (
                    <Link to={`/projects/${guardrail.projectId}`}>{guardrail.projectName}</Link>
                  )}
                </dd>
              </Fragment>
            ))}
          </dl>
        </section>
      ))}
    </CardBody>
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
