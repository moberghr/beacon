import { beforeEach, describe, it, expect, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { Route, Routes } from 'react-router-dom';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { toast } from 'sonner';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { permissionsForRoles, permissionsHandler } from '@/test/permissions';
import { ProjectMembershipManagement, ProjectRole } from '@/lib/enums';
import ProjectDetailPage from './ProjectDetailPage';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn(), warning: vi.fn() },
}));

function project(overrides: Record<string, unknown> = {}) {
  return {
    project: {
      id: 5,
      name: 'Ledger',
      description: null,
      totalTables: 3,
      qualityScore: null,
      codeReferenceCount: 0,
      lastScanAt: null,
      dataSources: [{ id: 11, name: 'ledger-db', type: 'Database', tableCount: 3, qualityScore: null }],
      repositories: [],
      hasDocumentation: false,
      requiresMembership: false,
      myRole: ProjectRole.Viewer,
      canManageMembers: false,
      openlyReachableDataSourceIds: [],
      sharedSourceProjects: [],
      sharedSourceGuardrails: [],
      membershipManagement: ProjectMembershipManagement.Beacon,
      ...overrides,
    },
  };
}

function signedIn(roles: string[]) {
  mswServer.use(permissionsForRoles(roles));
  return http.get('*/beacon/api/auth/me', () =>
    HttpResponse.json({
      userId: 'u1',
      displayName: 'Tester',
      email: 't@example.com',
      isAuthenticated: true,
      roles,
      realtimeEnabled: true,
      externalLogin: null,
    }),
  );
}

function renderPage() {
  return renderWithProviders(
    <Routes>
      <Route path="/projects/:id" element={<ProjectDetailPage />} />
    </Routes>,
    { initialEntries: ['/projects/5'] },
  );
}

const conflict = (detail: string) =>
  HttpResponse.json({ title: 'Conflict', status: 409, detail }, { status: 409, headers: { 'Content-Type': 'application/problem+json' } });

/** Project 7 (restricted, administered by Bea) and project 8 (open, no project admin) share ledger-db with project 5. */
const sharing = [
  { projectId: 7, name: 'Audit', requiresMembership: true, sharedDataSourceIds: [11], projectAdminNames: ['Bea Auditor'] },
  { projectId: 8, name: 'Sandbox', requiresMembership: false, sharedDataSourceIds: [11], projectAdminNames: [] },
];

describe('ProjectDetailPage access', () => {
  beforeEach(() => {
    vi.mocked(toast.success).mockClear();
    vi.mocked(toast.error).mockClear();
  });

  it('shows the Members tab to a caller who manages members, with the project members', async () => {
    mswServer.use(
      signedIn([]),
      http.get('*/beacon/api/projects/5', () =>
        HttpResponse.json(project({ myRole: ProjectRole.ProjectAdmin, canManageMembers: true, requiresMembership: true })),
      ),
      http.get('*/beacon/api/projects/5/members', () =>
        HttpResponse.json({
          items: [
            {
              userId: 3,
              displayName: 'Ada',
              role: ProjectRole.ProjectAdmin,
              isActive: true,
              addedAt: '2026-10-01T10:00:00Z',
              modifiedAt: null,
              canChange: true,
            },
          ],
          totalCount: 1,
          pageCount: 1,
        }),
      ),
    );

    renderPage();

    expect(await screen.findByText('Ledger')).toBeInTheDocument();
    expect(screen.getAllByText(/restricted/i).length).toBeGreaterThan(0);
    expect(screen.getByText(/your role: project admin/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('tab', { name: /members/i }));

    const table = await screen.findByRole('table', { name: /project members/i });
    expect(await within(table).findByText('Ada')).toBeInTheDocument();
    // A project admin who is not an Admin cannot restrict or open the project.
    expect(screen.queryByRole('radiogroup', { name: /project access/i })).not.toBeInTheDocument();
  });

  it('hides the Members tab and the link action from a caller who does not manage members', async () => {
    mswServer.use(
      signedIn([]),
      http.get('*/beacon/api/projects/5', () => HttpResponse.json(project())),
    );

    renderPage();

    expect(await screen.findByText('Ledger')).toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: /members/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /link data sources/i })).not.toBeInTheDocument();
  });

  it('hides the Members tab while the host manages membership, and the Admin is told where members are managed', async () => {
    mswServer.use(
      signedIn(['Admin']),
      http.get('*/beacon/api/projects/5', () =>
        HttpResponse.json(project({
          myRole: ProjectRole.ProjectAdmin,
          canManageMembers: false,
          membershipManagement: ProjectMembershipManagement.Host,
        })),
      ),
    );

    renderPage();

    expect(await screen.findByText(/members are managed by the host application/i)).toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: /members/i })).not.toBeInTheDocument();
    // Linking data sources needs the project-admin role, not Beacon-managed membership.
    expect(screen.getByRole('button', { name: /link data sources/i })).toBeInTheDocument();
  });

  it('lets an Admin restrict the project and lists the sources open projects still reach', async () => {
    let body: unknown = null;
    mswServer.use(
      signedIn(['Admin']),
      http.get('*/beacon/api/projects/5', () =>
        HttpResponse.json(project({ myRole: ProjectRole.ProjectAdmin, canManageMembers: true, openlyReachableDataSourceIds: [11] })),
      ),
      http.put('*/beacon/api/projects/5/access', async ({ request }) => {
        body = await request.json();
        return HttpResponse.json({ projectId: 5, requiresMembership: true });
      }),
    );

    renderPage();

    expect(await screen.findByText(/stay reachable through open projects/i)).toBeInTheDocument();
    expect(screen.getByText(/ledger-db is also linked to an open project/i)).toBeInTheDocument();

    fireEvent.click(screen.getByRole('radio', { name: /restricted/i }));
    fireEvent.click(await screen.findByRole('button', { name: /^restrict$/i }));

    await waitFor(() => expect(body).toEqual({ requiresMembership: true }));
  });

  it('links the data sources picked in the link modal', async () => {
    let body: unknown = null;
    mswServer.use(
      signedIn([]),
      http.get('*/beacon/api/projects/5', () =>
        HttpResponse.json(project({ myRole: ProjectRole.ProjectAdmin, canManageMembers: true })),
      ),
      http.get('*/beacon/api/data-sources', () =>
        HttpResponse.json({
          items: [
            { id: 11, name: 'ledger-db', dataSourceType: 'Database', databaseEngineType: 'PostgreSQL' },
            { id: 12, name: 'payments-db', dataSourceType: 'Database', databaseEngineType: 'MSSQL' },
          ],
          totalCount: 2,
          pageCount: 1,
        }),
      ),
      http.post('*/beacon/api/projects/5/data-sources', async ({ request }) => {
        body = await request.json();
        return HttpResponse.json({ projectId: 5, linkedDataSourceIds: [12] });
      }),
    );

    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: /link data sources/i }));
    const dialog = await screen.findByRole('dialog', { name: /link data sources to ledger/i });
    // The source the project already links is locked.
    const linked = await within(dialog).findByRole('checkbox', { name: /ledger-db/i });
    expect(linked).toBeDisabled();
    fireEvent.click(within(dialog).getByRole('checkbox', { name: /payments-db/i }));
    fireEvent.click(within(dialog).getByRole('button', { name: /^link data source$/i }));

    await waitFor(() => expect(body).toEqual({ dataSourceIds: [12] }));
  });

  it('shows the not-found state when the project answers 404', async () => {
    mswServer.use(
      signedIn([]),
      http.get('*/beacon/api/projects/5', () =>
        HttpResponse.json({ title: 'Not found', status: 404 }, { status: 404 }),
      ),
    );

    renderPage();

    expect(await screen.findByText('Project not found')).toBeInTheDocument();
    expect(screen.getByText(/you do not have access to it/i)).toBeInTheDocument();
    expect(screen.queryByText(/failed to load project/i)).not.toBeInTheDocument();
  });

  it('tells an Admin when membership is unavailable rather than host-managed', async () => {
    mswServer.use(
      signedIn(['Admin']),
      http.get('*/beacon/api/projects/5', () =>
        HttpResponse.json(project({
          myRole: ProjectRole.ProjectAdmin,
          canManageMembers: false,
          membershipManagement: ProjectMembershipManagement.Unavailable,
        })),
      ),
    );

    renderPage();

    expect(await screen.findByText(/project membership is not available in this deployment/i)).toBeInTheDocument();
    expect(screen.queryByText(/managed by the host application/i)).not.toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: /members/i })).not.toBeInTheDocument();
  });

  it('lets an Admin open a restricted project', async () => {
    let body: unknown = null;
    mswServer.use(
      signedIn(['Admin']),
      http.get('*/beacon/api/projects/5', () =>
        HttpResponse.json(project({ myRole: ProjectRole.ProjectAdmin, canManageMembers: true, requiresMembership: true })),
      ),
      http.put('*/beacon/api/projects/5/access', async ({ request }) => {
        body = await request.json();
        return HttpResponse.json({ projectId: 5, requiresMembership: false, sharedSourceProjects: [] });
      }),
    );

    renderPage();

    const access = await screen.findByRole('radiogroup', { name: /project access/i });
    expect(within(access).getByRole('radio', { name: /restricted/i })).toHaveAttribute('aria-checked', 'true');
    fireEvent.click(within(access).getByRole('radio', { name: /^open$/i }));
    fireEvent.click(await screen.findByRole('button', { name: /^open$/i }));

    await waitFor(() => expect(body).toEqual({ requiresMembership: false }));
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Project opened'));
  });

  it('keeps the restrict dialog open and says why when the server refuses (409)', async () => {
    mswServer.use(
      signedIn(['Admin']),
      http.get('*/beacon/api/projects/5', () =>
        HttpResponse.json(project({ myRole: ProjectRole.ProjectAdmin, canManageMembers: true })),
      ),
      http.put('*/beacon/api/projects/5/access', () =>
        conflict('Projects cannot be restricted: project membership is not available in this deployment.'),
      ),
    );

    renderPage();

    fireEvent.click(await screen.findByRole('radio', { name: /restricted/i }));
    fireEvent.click(await screen.findByRole('button', { name: /^restrict$/i }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith(expect.stringContaining('cannot be restricted')));
    expect(screen.getByRole('button', { name: /^restrict$/i })).toBeInTheDocument();
    expect(screen.getByRole('radio', { name: /^open$/i })).toHaveAttribute('aria-checked', 'true');
  });

  it('shows a UserAdmin who is not the project admin the Members tab and the access card, without unlinking', async () => {
    mswServer.use(
      signedIn([]),
      permissionsHandler({ permissions: ['UserAdmin'] }),
      http.get('*/beacon/api/projects/5', () =>
        HttpResponse.json(project({ myRole: ProjectRole.Viewer, canManageMembers: false, sharedSourceProjects: sharing })),
      ),
      http.get('*/beacon/api/projects/5/members', () => HttpResponse.json({ items: [], totalCount: 0, pageCount: 0 })),
    );

    renderPage();

    expect(await screen.findByRole('radiogroup', { name: /project access/i })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: /members/i })).toBeInTheDocument();
    // Unlinking a data source from another project stays an Admin's.
    expect(screen.queryByRole('button', { name: /^unlink /i })).not.toBeInTheDocument();
    // Linking needs the project-admin role.
    expect(screen.queryByRole('button', { name: /link data sources/i })).not.toBeInTheDocument();
  });

  it('does not offer a UserAdmin to open a restricted project, which is an Admin\'s', async () => {
    mswServer.use(
      signedIn([]),
      permissionsHandler({ permissions: ['UserAdmin'] }),
      http.get('*/beacon/api/projects/5', () =>
        HttpResponse.json(project({ myRole: ProjectRole.Viewer, canManageMembers: false, requiresMembership: true })),
      ),
      http.get('*/beacon/api/projects/5/members', () => HttpResponse.json({ items: [], totalCount: 0, pageCount: 0 })),
    );

    renderPage();

    expect(await screen.findByText(/only an admin can open a restricted project/i)).toBeInTheDocument();
    expect(screen.queryByRole('radiogroup', { name: /project access/i })).not.toBeInTheDocument();
  });

  it('shows a UserAdmin no Members tab while the host manages membership', async () => {
    mswServer.use(
      signedIn([]),
      permissionsHandler({ permissions: ['UserAdmin'] }),
      http.get('*/beacon/api/projects/5', () =>
        HttpResponse.json(project({ membershipManagement: ProjectMembershipManagement.Host })),
      ),
    );

    renderPage();

    expect(await screen.findByRole('radiogroup', { name: /project access/i })).toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: /members/i })).not.toBeInTheDocument();
  });

  it('offers a repository access token to a SecurityAdmin only', async () => {
    const repositories = [
      { id: 21, name: 'ledger-api', url: 'https://example.test/ledger-api', scanStatus: null, lastScanAt: null, referenceCount: 0, hasAccessToken: false },
    ];
    mswServer.use(
      signedIn([]),
      http.get('*/beacon/api/projects/5', () => HttpResponse.json(project({ repositories }))),
    );

    const view = renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: /repositories/i }));
    expect(await screen.findByText('ledger-api')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /set token for ledger-api/i })).not.toBeInTheDocument();
    view.unmount();

    mswServer.use(permissionsHandler({ permissions: ['SecurityAdmin'] }));
    renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: /repositories/i }));
    expect(await screen.findByRole('button', { name: /set token for ledger-api/i })).toBeInTheDocument();
  });

  it('marks a repository whose last scan hit a limit as partial, with the reasons', async () => {
    const repositories = [
      {
        id: 21, name: 'ledger-api', url: 'https://example.test/ledger-api', scanStatus: 'Completed', lastScanAt: null,
        referenceCount: 4, hasAccessToken: false, partialScan: true, partialScanReasons: ['only the first 3 of 7 files were scanned'],
      },
      {
        id: 22, name: 'billing-api', url: 'https://example.test/billing-api', scanStatus: 'Completed', lastScanAt: null,
        referenceCount: 9, hasAccessToken: false, partialScan: false, partialScanReasons: [],
      },
    ];
    mswServer.use(
      signedIn([]),
      http.get('*/beacon/api/projects/5', () => HttpResponse.json(project({ repositories }))),
    );

    renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: /repositories/i }));

    expect(await screen.findByText('A repository was only partly scanned')).toBeInTheDocument();
    expect(screen.getByText(/only the first 3 of 7 files were scanned/)).toBeInTheDocument();
    expect(screen.getAllByText('Partial')).toHaveLength(1);
  });

  it('never shows the access card or the projects sharing its sources to a non-Admin, even a project admin', async () => {
    mswServer.use(
      signedIn([]),
      http.get('*/beacon/api/projects/5', () =>
        HttpResponse.json(project({ myRole: ProjectRole.ProjectAdmin, canManageMembers: true, sharedSourceProjects: sharing })),
      ),
    );

    renderPage();

    expect(await screen.findByText('Ledger')).toBeInTheDocument();
    expect(screen.queryByText('Access')).not.toBeInTheDocument();
    expect(screen.queryByRole('radiogroup', { name: /project access/i })).not.toBeInTheDocument();
    expect(screen.queryByText(/other projects using these data sources/i)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^unlink /i })).not.toBeInTheDocument();
  });

  it('shows an Admin the other projects sharing its sources, with their project admins, and unlinks one after confirming', async () => {
    let detailReads = 0;
    let unlinked: string | null = null;
    mswServer.use(
      signedIn(['Admin']),
      http.get('*/beacon/api/projects/5', () => {
        detailReads += 1;
        return HttpResponse.json(project({ myRole: ProjectRole.ProjectAdmin, canManageMembers: true, sharedSourceProjects: sharing }));
      }),
      http.delete('*/beacon/api/projects/:projectId/data-sources/:dataSourceId', ({ params }) => {
        unlinked = `${params.projectId}/${params.dataSourceId}`;
        return HttpResponse.json({ projectId: 7, dataSourceId: 11, leftUnlinked: false });
      }),
    );

    renderPage();

    const table = await screen.findByRole('table', { name: /other projects using these data sources/i });
    expect(within(table).getByRole('link', { name: 'Audit' })).toHaveAttribute('href', '/projects/7');
    expect(within(table).getByText('Bea Auditor')).toBeInTheDocument();
    expect(within(table).getByText('No project admin')).toBeInTheDocument();
    expect(within(table).getAllByText(/restricted/i)).toHaveLength(1);

    fireEvent.click(within(table).getByRole('button', { name: 'Unlink ledger-db from Audit' }));
    expect(await screen.findByText(/members of audit no longer reach it/i)).toBeInTheDocument();
    const readsBefore = detailReads;
    fireEvent.click(screen.getByRole('button', { name: /^unlink$/i }));

    await waitFor(() => expect(unlinked).toBe('7/11'));
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Data source unlinked'));
    await waitFor(() => expect(detailReads).toBeGreaterThan(readsBefore));
    await waitFor(() => expect(screen.queryByRole('button', { name: /^unlink$/i })).not.toBeInTheDocument());
  });

  it('says why an unlink was refused (409), keeps the dialog, and reloads the links', async () => {
    let detailReads = 0;
    mswServer.use(
      signedIn(['Admin']),
      http.get('*/beacon/api/projects/5', () => {
        detailReads += 1;
        return HttpResponse.json(project({ myRole: ProjectRole.ProjectAdmin, canManageMembers: true, sharedSourceProjects: sharing }));
      }),
      http.delete('*/beacon/api/projects/7/data-sources/11', () => conflict("The data source's links changed concurrently; retry.")),
    );

    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Unlink ledger-db from Audit' }));
    const readsBefore = detailReads;
    fireEvent.click(await screen.findByRole('button', { name: /^unlink$/i }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith(expect.stringContaining('retry')));
    await waitFor(() => expect(detailReads).toBeGreaterThan(readsBefore));
    expect(screen.getByRole('button', { name: /^unlink$/i })).toBeInTheDocument();
  });

  it("shows an Admin each shared source's effective guardrails and the project that sets each", async () => {
    mswServer.use(
      signedIn(['Admin']),
      http.get('*/beacon/api/projects/5', () =>
        HttpResponse.json(project({
          myRole: ProjectRole.ProjectAdmin,
          canManageMembers: true,
          sharedSourceProjects: sharing,
          sharedSourceGuardrails: [
            {
              dataSourceId: 11,
              dataSourceName: 'ledger-db',
              guardrails: [
                { setting: 'enforceReadOnly', value: 'true', projectId: 5, projectName: 'Ledger' },
                { setting: 'enablePiiDetection', value: 'true', projectId: 7, projectName: 'Audit' },
                { setting: 'maxRowLimit', value: '500', projectId: 7, projectName: 'Audit' },
                { setting: 'statementTimeoutSeconds', value: '30', projectId: 5, projectName: 'Ledger' },
                { setting: 'maxExplainCost', value: 'none', projectId: 5, projectName: 'Ledger' },
                { setting: 'customPiiPatterns', value: '2', projectId: 8, projectName: 'Sandbox' },
              ],
            },
          ],
        })),
      ),
    );

    renderPage();

    expect(await screen.findByText('Guardrails of shared data sources')).toBeInTheDocument();
    const source = screen.getByRole('region', { name: 'Guardrails of ledger-db' });
    const row = (label: string) => within(source).getByText(label).nextElementSibling as HTMLElement;

    expect(row('Read-only')).toHaveTextContent('on · set by this project');
    expect(row('PII detection')).toHaveTextContent('on · set by Audit');
    expect(within(row('PII detection')).getByRole('link', { name: 'Audit' })).toHaveAttribute('href', '/projects/7');
    expect(row('Row limit')).toHaveTextContent('500 rows · set by Audit');
    expect(row('Statement timeout')).toHaveTextContent('30 s · set by this project');
    expect(row('Plan cost limit')).toHaveTextContent('no limit');
    expect(row('Custom PII patterns')).toHaveTextContent('2 patterns · set by Sandbox');
  });

  it('labels every guardrail the server describes, and words one pattern and one probe in the singular', async () => {
    mswServer.use(
      signedIn(['Admin']),
      http.get('*/beacon/api/projects/5', () =>
        HttpResponse.json(project({
          myRole: ProjectRole.ProjectAdmin,
          canManageMembers: true,
          sharedSourceProjects: sharing,
          sharedSourceGuardrails: [
            {
              dataSourceId: 11,
              dataSourceName: 'ledger-db',
              guardrails: [
                { setting: 'maxResultBytes', value: '1048576', projectId: 7, projectName: 'Audit' },
                { setting: 'customPiiPatterns', value: '1', projectId: 8, projectName: 'Sandbox' },
                { setting: 'enableValueGrounding', value: 'false', projectId: 7, projectName: 'Audit' },
                { setting: 'valueGroundingMaxProbes', value: '1', projectId: 5, projectName: 'Ledger' },
                { setting: 'enableSampleValueCollection', value: 'false', projectId: 8, projectName: 'Sandbox' },
              ],
            },
          ],
        })),
      ),
    );

    renderPage();

    const source = await screen.findByRole('region', { name: 'Guardrails of ledger-db' });
    const row = (label: string) => within(source).getByText(label).nextElementSibling as HTMLElement;

    expect(row('Result size limit')).toHaveTextContent(`${(1048576).toLocaleString()} bytes · set by Audit`);
    expect(row('Custom PII patterns')).toHaveTextContent('1 pattern · set by Sandbox');
    expect(row('Value grounding')).toHaveTextContent('off · set by Audit');
    expect(row('Value probes')).toHaveTextContent('1 probe · set by this project');
    expect(row('Sample value collection')).toHaveTextContent('off · set by Sandbox');
    expect(within(source).queryByText(/^(enableValueGrounding|valueGroundingMaxProbes|enableSampleValueCollection)$/)).not.toBeInTheDocument();
  });

  it('shows no guardrails section when no other project shares its sources', async () => {
    mswServer.use(
      signedIn(['Admin']),
      http.get('*/beacon/api/projects/5', () =>
        HttpResponse.json(project({ myRole: ProjectRole.ProjectAdmin, canManageMembers: true })),
      ),
    );

    renderPage();

    expect(await screen.findByRole('radiogroup', { name: /project access/i })).toBeInTheDocument();
    expect(screen.queryByText('Guardrails of shared data sources')).not.toBeInTheDocument();
  });
});

describe('ProjectDetailPage documentation', () => {
  const RETURNS_POLICY = { id: 3, sourceKey: 'docs', path: 'guides/returns.md', title: 'Returns policy', importedTime: '2026-10-01T10:00:00Z' };

  // No host-imported document unless a test serves some.
  beforeEach(() => {
    mswServer.use(http.get('*/beacon/api/projects/5/imported-documents', () => HttpResponse.json({ documents: [] })));
  });

  function serveImported() {
    mswServer.use(
      http.get('*/beacon/api/projects/5/imported-documents', () => HttpResponse.json({ documents: [RETURNS_POLICY] })),
      http.get('*/beacon/api/projects/5/imported-documents/3', () =>
        HttpResponse.json({ document: { ...RETURNS_POLICY, content: '# Returns\n\nItems go back within 30 days.', frontmatterJson: null } })),
    );
  }

  function documentation(canCurate: boolean, editedTime: string | null = null) {
    return {
      latest: {
        id: 1,
        generatedAt: '2026-10-01T10:00:00Z',
        generatedByModel: 'model',
        dataSourcesAnalyzed: 1,
        tablesAnalyzed: 3,
        codeReferencesAnalyzed: 0,
        inputTokens: 10,
        outputTokens: 20,
        estimatedCost: 0,
        generationDuration: '00:00:05',
        sections: [{ id: 4, sectionType: 0, title: 'Overview', content: 'The ledger.', sortOrder: 0, editedTime }],
      },
      history: [],
      canCurate,
    };
  }

  it('lets a curator edit a documentation section', async () => {
    mswServer.use(
      signedIn([]),
      http.get('*/beacon/api/projects/5', () => HttpResponse.json(project({ hasDocumentation: true }))),
      http.get('*/beacon/api/projects/5/documentation', () => HttpResponse.json(documentation(true))),
    );

    renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: /documentation/i }));

    expect(await screen.findByRole('button', { name: 'Edit Overview' })).toBeInTheDocument();
    expect(screen.queryByText(/regenerates its documentation/)).not.toBeInTheDocument();
  });

  it('marks a section a person edited as curated, and no other', async () => {
    mswServer.use(
      signedIn([]),
      http.get('*/beacon/api/projects/5', () => HttpResponse.json(project({ hasDocumentation: true }))),
      http.get('*/beacon/api/projects/5/documentation', () => HttpResponse.json(documentation(false, '2026-10-20T08:00:00Z'))),
    );

    renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: /documentation/i }));

    expect(await screen.findByText('Curated')).toBeInTheDocument();
  });

  it('shows no curated mark on generated sections', async () => {
    mswServer.use(
      signedIn([]),
      http.get('*/beacon/api/projects/5', () => HttpResponse.json(project({ hasDocumentation: true }))),
      http.get('*/beacon/api/projects/5/documentation', () => HttpResponse.json(documentation(false))),
    );

    renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: /documentation/i }));

    expect(await screen.findByText('The ledger.')).toBeInTheDocument();
    expect(screen.queryByText('Curated')).not.toBeInTheDocument();
  });

  it('shows anyone else the documentation without the edit action, and says who edits it', async () => {
    mswServer.use(
      signedIn([]),
      http.get('*/beacon/api/projects/5', () => HttpResponse.json(project({ hasDocumentation: true }))),
      http.get('*/beacon/api/projects/5/documentation', () => HttpResponse.json(documentation(false))),
    );

    renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: /documentation/i }));

    expect(await screen.findByText('The ledger.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Edit Overview' })).not.toBeInTheDocument();
    expect(screen.getByText('Only a project admin of this project (or an Admin) edits or regenerates its documentation.')).toBeInTheDocument();
  });
  it('shows the gateway the documents its host imported, not "no documentation", and opens one', async () => {
    mswServer.use(
      signedIn(['Admin']),
      permissionsHandler({ isAdmin: true, gatewayMode: true }),
      http.get('*/beacon/api/projects/5', () => HttpResponse.json(project())),
      http.get('*/beacon/api/projects/5/documentation', () => HttpResponse.json({ latest: null, history: [], canCurate: false })),
    );
    serveImported();

    renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: /documentation/i }));

    fireEvent.click(await screen.findByRole('button', { name: /Returns policy/ }));
    expect(await screen.findByText('Items go back within 30 days.')).toBeInTheDocument();
    expect(screen.getByText('guides/returns.md')).toBeInTheDocument();
    expect(screen.queryByText('No documentation generated yet')).not.toBeInTheDocument();
  });

  it('lists the imported documents after the generated documentation', async () => {
    mswServer.use(
      signedIn([]),
      http.get('*/beacon/api/projects/5', () => HttpResponse.json(project({ hasDocumentation: true }))),
      http.get('*/beacon/api/projects/5/documentation', () => HttpResponse.json(documentation(false))),
    );
    serveImported();

    renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: /documentation/i }));

    expect(await screen.findByText('The ledger.')).toBeInTheDocument();
    expect(screen.getByText('Imported documents')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Returns policy/ })).toHaveAttribute('aria-expanded', 'false');
  });

  it('still lists the imported documents when the generated documentation fails to load', async () => {
    mswServer.use(
      signedIn(['Admin']),
      permissionsHandler({ isAdmin: true, gatewayMode: true }),
      http.get('*/beacon/api/projects/5', () => HttpResponse.json(project())),
      http.get('*/beacon/api/projects/5/documentation', () => HttpResponse.json({ detail: 'boom' }, { status: 500 })),
    );
    serveImported();

    renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: /documentation/i }));

    expect(await screen.findByText('Failed to load documentation')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Returns policy/ })).toBeInTheDocument();
  });

  it('says no documentation was generated when there is none and the host imported none', async () => {
    mswServer.use(
      signedIn([]),
      http.get('*/beacon/api/projects/5', () => HttpResponse.json(project())),
      http.get('*/beacon/api/projects/5/documentation', () => HttpResponse.json({ latest: null, history: [], canCurate: false })),
    );

    renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: /documentation/i }));

    expect(await screen.findByText('No documentation generated yet')).toBeInTheDocument();
    expect(screen.queryByText('Imported documents')).not.toBeInTheDocument();
  });
});

describe('ProjectDetailPage repository scan', () => {
  beforeEach(() => {
    vi.mocked(toast.success).mockClear();
    vi.mocked(toast.error).mockClear();
    vi.mocked(toast.warning).mockClear();
  });

  const repositories = [
    { id: 21, name: 'ledger-api', url: 'https://example.test/ledger-api', scanStatus: null, lastScanAt: null, referenceCount: 0, hasAccessToken: false },
  ];

  function task(state: number, overrides: Record<string, unknown> = {}) {
    return {
      taskId: 51,
      jobId: 'job-3',
      state,
      createdTime: '2026-10-24T12:00:00Z',
      startedTime: null,
      finishedTime: null,
      failureCode: null,
      warningCount: null,
      ...overrides,
    };
  }

  const started = http.post('*/beacon/api/projects/5/scans', () =>
    HttpResponse.json({ taskId: 51, jobId: 'job-3', state: 0 }, { status: 202 }),
  );

  it('offers the scan to an Editor of the project only', async () => {
    mswServer.use(
      signedIn([]),
      http.get('*/beacon/api/projects/5', () => HttpResponse.json(project({ repositories, myRole: ProjectRole.Viewer }))),
    );

    const view = renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: /repositories/i }));
    expect(await screen.findByText('ledger-api')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /scan repositories/i })).not.toBeInTheDocument();
    view.unmount();

    mswServer.use(http.get('*/beacon/api/projects/5', () => HttpResponse.json(project({ repositories, myRole: ProjectRole.Editor }))));
    renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: /repositories/i }));
    expect(await screen.findByRole('button', { name: /scan repositories/i })).toBeEnabled();
  });

  it('starts a background scan, shows its progress, and reads the project again when it finishes', async () => {
    let reads = 0;
    let projectReads = 0;
    mswServer.use(
      signedIn([]),
      http.get('*/beacon/api/projects/5', () => {
        projectReads += 1;
        return HttpResponse.json(project({ repositories, myRole: ProjectRole.Editor }));
      }),
      started,
      http.get('*/beacon/api/projects/5/scans/51', () => {
        reads += 1;
        return HttpResponse.json(reads === 1 ? task(1) : task(2, { warningCount: 0 }));
      }),
    );

    renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: /repositories/i }));
    fireEvent.click(await screen.findByRole('button', { name: /scan repositories/i }));

    expect(await screen.findByRole('button', { name: /scanning/i })).toBeDisabled();
    const before = projectReads;
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Repositories scanned'), { timeout: 5000 });
    await waitFor(() => expect(projectReads).toBeGreaterThan(before));
    expect(await screen.findByRole('button', { name: /scan repositories/i })).toBeEnabled();
  });

  it('says how many repositories failed or were only partly scanned', async () => {
    mswServer.use(
      signedIn([]),
      http.get('*/beacon/api/projects/5', () => HttpResponse.json(project({ repositories, myRole: ProjectRole.Editor }))),
      started,
      http.get('*/beacon/api/projects/5/scans/51', () => HttpResponse.json(task(2, { warningCount: 2 }))),
    );

    renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: /repositories/i }));
    fireEvent.click(await screen.findByRole('button', { name: /scan repositories/i }));

    await waitFor(() => expect(toast.warning).toHaveBeenCalledWith(expect.stringContaining('2 repositories failed or were only partly scanned')));
  });

  it("says why the scan did not start (409) or did not run", async () => {
    mswServer.use(
      signedIn([]),
      http.get('*/beacon/api/projects/5', () => HttpResponse.json(project({ repositories, myRole: ProjectRole.Editor }))),
      http.post('*/beacon/api/projects/5/scans', () => conflict("A scan of this project's repositories is already queued or running.")),
    );

    const view = renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: /repositories/i }));
    fireEvent.click(await screen.findByRole('button', { name: /scan repositories/i }));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith(expect.stringContaining('already queued or running')));
    view.unmount();

    mswServer.use(
      started,
      http.get('*/beacon/api/projects/5/scans/51', () => HttpResponse.json(task(4, { failureCode: 'forbidden' }))),
    );
    renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: /repositories/i }));
    fireEvent.click(await screen.findByRole('button', { name: /scan repositories/i }));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith('You can no longer change this project, so its repositories were not scanned.'));
    expect(toast.success).not.toHaveBeenCalled();
  });
});
