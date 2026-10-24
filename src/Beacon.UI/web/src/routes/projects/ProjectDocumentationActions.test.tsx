import { afterEach, beforeEach, describe, it, expect, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { Route, Routes } from 'react-router-dom';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { toast } from 'sonner';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { permissionsForRoles } from '@/test/permissions';
import { ProjectMembershipManagement, ProjectRole } from '@/lib/enums';
import ProjectDetailPage from './ProjectDetailPage';
import { fileNameOf } from './queries';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn(), warning: vi.fn(), info: vi.fn() },
}));

const PROJECT = {
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
    hasDocumentation: true,
    requiresMembership: false,
    myRole: ProjectRole.ProjectAdmin,
    canManageMembers: true,
    openlyReachableDataSourceIds: [],
    sharedSourceProjects: [],
    sharedSourceGuardrails: [],
    membershipManagement: ProjectMembershipManagement.Beacon,
  },
};

function documentation(canCurate: boolean, hasLatest = true) {
  return {
    latest: hasLatest
      ? {
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
          sections: [{ id: 4, sectionType: 0, title: 'Overview', content: 'The ledger.', sortOrder: 0 }],
        }
      : null,
    history: [],
    canCurate,
  };
}

function task(state: number, overrides: Record<string, unknown> = {}) {
  return {
    taskId: 31,
    jobId: 'job-1',
    state,
    createdTime: '2026-10-24T12:00:00Z',
    startedTime: null,
    finishedTime: null,
    failureCode: null,
    warningCount: null,
    ...overrides,
  };
}

function serve(canCurate: boolean, hasLatest = true, ...extra: Parameters<typeof mswServer.use>) {
  mswServer.use(
    ...extra,
    permissionsForRoles([]),
    http.get('*/beacon/api/auth/me', () =>
      HttpResponse.json({ userId: 'u1', displayName: 'Tester', email: 't@example.com', isAuthenticated: true, roles: [], realtimeEnabled: true, externalLogin: null }),
    ),
    http.get('*/beacon/api/projects/5', () => HttpResponse.json(PROJECT)),
    http.get('*/beacon/api/projects/5/documentation', () => HttpResponse.json(documentation(canCurate, hasLatest))),
    http.get('*/beacon/api/projects/5/imported-documents', () => HttpResponse.json({ documents: [] })),
  );
  renderWithProviders(
    <Routes>
      <Route path="/projects/:id" element={<ProjectDetailPage />} />
    </Routes>,
    { initialEntries: ['/projects/5'] },
  );
}

async function openDocumentationTab() {
  fireEvent.click(await screen.findByRole('tab', { name: /documentation/i }));
}

const started = http.post('*/beacon/api/projects/5/documentation-generations', () =>
  HttpResponse.json({ taskId: 31, jobId: 'job-1', state: 0 }, { status: 202 }),
);

describe('Project documentation actions', () => {
  beforeEach(() => {
    vi.mocked(toast.success).mockClear();
    vi.mocked(toast.error).mockClear();
    vi.mocked(toast.warning).mockClear();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('starts a background generation after a confirmation, shows its progress, and reads the documentation again when it succeeds', async () => {
    let reads = 0;
    let documentationReads = 0;
    serve(
      true,
      true,
      started,
      http.get('*/beacon/api/projects/5/documentation-generations/31', () => {
        reads += 1;
        return HttpResponse.json(reads === 1 ? task(1) : task(2));
      }),
      http.get('*/beacon/api/projects/5/documentation', () => {
        documentationReads += 1;
        return HttpResponse.json(documentation(true));
      }),
    );
    await openDocumentationTab();

    fireEvent.click(await screen.findByRole('button', { name: 'Regenerate documentation' }));
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Regenerate' }));

    expect(await screen.findByRole('button', { name: /generating documentation/i })).toBeDisabled();
    const before = documentationReads;
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Documentation generated'), { timeout: 5000 });
    await waitFor(() => expect(documentationReads).toBeGreaterThan(before));
    expect(await screen.findByRole('button', { name: 'Regenerate documentation' })).toBeEnabled();
  });

  it('offers to generate the first documentation without a confirmation', async () => {
    serve(true, false, started, http.get('*/beacon/api/projects/5/documentation-generations/31', () => HttpResponse.json(task(2))));
    await openDocumentationTab();

    fireEvent.click(await screen.findByRole('button', { name: 'Generate documentation' }));

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Documentation generated'), { timeout: 5000 });
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('says why a generation failed, by its code', async () => {
    serve(
      true,
      true,
      started,
      http.get('*/beacon/api/projects/5/documentation-generations/31', () => HttpResponse.json(task(3, { failureCode: 'forbidden' }))),
    );
    await openDocumentationTab();

    fireEvent.click(await screen.findByRole('button', { name: 'Regenerate documentation' }));
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Regenerate' }));

    await waitFor(() =>
      expect(toast.error).toHaveBeenCalledWith(expect.stringContaining('no longer curate this project')),
    { timeout: 5000 });
  });

  it('warns when a generation of the project is already running (429)', async () => {
    serve(
      true,
      true,
      http.post('*/beacon/api/projects/5/documentation-generations', () =>
        HttpResponse.json({ title: 'The documentation of this project is already being generated.', status: 429 }, { status: 429 }),
      ),
    );
    await openDocumentationTab();

    fireEvent.click(await screen.findByRole('button', { name: 'Regenerate documentation' }));
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Regenerate' }));

    await waitFor(() => expect(toast.warning).toHaveBeenCalledWith('The documentation of this project is already being generated.'));
    expect(toast.error).not.toHaveBeenCalled();
  });

  it('does not offer generating, or instructing, to someone who may not curate, but still exports', async () => {
    serve(false);
    await openDocumentationTab();

    expect(await screen.findByRole('button', { name: 'Export Markdown' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /generate documentation|regenerate documentation/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /with an instruction/i })).not.toBeInTheDocument();
  });

  it('downloads the latest documentation as a file named by the server', async () => {
    const click = vi.fn();
    const create = vi.fn(() => 'blob:doc');
    const revoke = vi.fn();
    vi.stubGlobal('URL', Object.assign(URL, { createObjectURL: create, revokeObjectURL: revoke }));
    const anchorClick = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(click);
    let asHtml: string | null = null;
    serve(
      true,
      true,
      http.get('*/beacon/api/projects/documentation/1/export', ({ request }) => {
        asHtml = new URL(request.url).searchParams.get('asHtml');
        return new HttpResponse('<h1>Ledger</h1>', {
          headers: { 'Content-Type': 'text/html', 'Content-Disposition': 'attachment; filename=Ledger-docs.html' },
        });
      }),
    );
    await openDocumentationTab();

    fireEvent.click(await screen.findByRole('button', { name: 'Export HTML' }));

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Exported Ledger-docs.html'));
    expect(asHtml).toBe('true');
    expect(click).toHaveBeenCalledTimes(1);
    expect(revoke).toHaveBeenCalledWith('blob:doc');
    anchorClick.mockRestore();
  });

  it('reports a failed export', async () => {
    serve(true, true, http.get('*/beacon/api/projects/documentation/1/export', () => HttpResponse.json({ title: 'Boom', status: 500 }, { status: 500 })));
    await openDocumentationTab();

    fireEvent.click(await screen.findByRole('button', { name: 'Export Markdown' }));

    await waitFor(() => expect(toast.error).toHaveBeenCalled());
  });

  it('shows what an instruction would change and saves it only after a confirmation', async () => {
    let saved: { content: string } | null = null;
    let instruction: string | null = null;
    serve(
      true,
      true,
      http.post('*/beacon/api/projects/documentation-sections/4/instruct', async ({ request }) => {
        instruction = ((await request.json()) as { instruction: string }).instruction;
        return HttpResponse.json({ updatedContent: 'The ledger (legacy tables deprecated).' });
      }),
      http.put('*/beacon/api/projects/documentation-sections/4', async ({ request }) => {
        saved = (await request.json()) as { content: string };
        return new HttpResponse(null, { status: 204 });
      }),
    );
    await openDocumentationTab();

    fireEvent.click(await screen.findByRole('button', { name: 'Change Overview with an instruction' }));
    fireEvent.change(await screen.findByLabelText('Instruction'), { target: { value: 'deprecate legacy tables' } });
    fireEvent.click(screen.getByRole('button', { name: 'Propose change' }));

    expect(await screen.findByDisplayValue('The ledger (legacy tables deprecated).')).toHaveAttribute('readonly');
    expect(instruction).toBe('deprecate legacy tables');
    expect(saved).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Apply change' }));
    expect(saved).toBeNull();
    fireEvent.click(within(await screen.findByRole('dialog', { name: /replace this section/i })).getByRole('button', { name: 'Replace section' }));

    await waitFor(() => expect(saved).toEqual({ content: 'The ledger (legacy tables deprecated).' }));
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("Updated section 'Overview'"));
  });

  it('keeps the section when the confirmation is cancelled', async () => {
    let saved = false;
    serve(
      true,
      true,
      http.post('*/beacon/api/projects/documentation-sections/4/instruct', () => HttpResponse.json({ updatedContent: 'New text.' })),
      http.put('*/beacon/api/projects/documentation-sections/4', () => {
        saved = true;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    await openDocumentationTab();

    fireEvent.click(await screen.findByRole('button', { name: 'Change Overview with an instruction' }));
    fireEvent.change(await screen.findByLabelText('Instruction'), { target: { value: 'rewrite' } });
    fireEvent.click(screen.getByRole('button', { name: 'Propose change' }));
    await screen.findByDisplayValue('New text.');
    fireEvent.click(screen.getByRole('button', { name: 'Apply change' }));
    fireEvent.click(within(await screen.findByRole('dialog', { name: /replace this section/i })).getByRole('button', { name: 'Cancel' }));

    expect(saved).toBe(false);
  });
});

describe('fileNameOf', () => {
  it('reads a plain and an encoded file name, and none', () => {
    expect(fileNameOf('attachment; filename=Ledger-docs.md')).toBe('Ledger-docs.md');
    expect(fileNameOf('attachment; filename="a b.md"')).toBe('a b.md');
    expect(fileNameOf("attachment; filename=x.md; filename*=UTF-8''%C5%A1.md")).toBe('š.md');
    expect(fileNameOf(null)).toBeNull();
  });
});
