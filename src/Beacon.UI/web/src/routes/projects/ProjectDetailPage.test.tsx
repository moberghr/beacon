import { beforeEach, describe, it, expect, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { Route, Routes } from 'react-router-dom';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { toast } from 'sonner';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { ProjectMembershipManagement, ProjectRole } from '@/lib/enums';
import ProjectDetailPage from './ProjectDetailPage';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn() },
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
      membershipManagement: ProjectMembershipManagement.Beacon,
      ...overrides,
    },
  };
}

function signedIn(roles: string[]) {
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
            { userId: 3, displayName: 'Ada', role: ProjectRole.ProjectAdmin, isActive: true, addedAt: '2026-10-01T10:00:00Z', modifiedAt: null },
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
});
