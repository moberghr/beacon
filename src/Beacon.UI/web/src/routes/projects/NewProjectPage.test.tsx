import { beforeEach, describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { Route, Routes } from 'react-router-dom';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { toast } from 'sonner';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { permissionsHandler } from '@/test/permissions';
import NewProjectPage from './NewProjectPage';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn() },
}));

const noDataSources = http.get('*/beacon/api/data-sources', () => HttpResponse.json({ items: [], totalCount: 0, pageCount: 0 }));
const csrf = http.get('*/beacon/api/csrf', () => new HttpResponse(null, { status: 204 }));

function renderAndCreate(requiresMembership: boolean) {
  let body: unknown = null;
  mswServer.use(
    noDataSources,
    csrf,
    http.post('*/beacon/api/projects', async ({ request }) => {
      body = await request.json();
      return HttpResponse.json({ projectId: 9, requiresMembership }, { status: 201 });
    }),
  );

  renderWithProviders(
    <Routes>
      <Route path="/projects/new" element={<NewProjectPage />} />
      <Route path="/projects/:id" element={<div>project page</div>} />
    </Routes>,
    { initialEntries: ['/projects/new'] },
  );

  fireEvent.input(screen.getByLabelText(/project name/i), { target: { value: 'Ledger' } });
  fireEvent.click(screen.getByRole('button', { name: /create project/i }));
  return () => body;
}

describe('NewProjectPage', () => {
  beforeEach(() => {
    vi.mocked(toast.success).mockClear();
    vi.mocked(toast.error).mockClear();
  });

  it('says new projects are restricted, with the creator as project admin, before anything is created', () => {
    mswServer.use(noDataSources);
    renderWithProviders(<NewProjectPage />);
    expect(screen.getByText('New projects are restricted; you will be the project admin')).toBeInTheDocument();
    expect(screen.getByText(/where project membership is not kept in beacon, the project is created open/i)).toBeInTheDocument();
  });

  it('confirms a restricted project and opens it', async () => {
    const sent = renderAndCreate(true);

    expect(await screen.findByText('project page')).toBeInTheDocument();
    expect(sent()).toMatchObject({ name: 'Ledger' });
    expect(toast.success).toHaveBeenCalledWith('Project created', {
      description: 'It is restricted to its members, and you are its project admin.',
    });
  });

  it('lets only a SecurityAdmin set a repository access token', async () => {
    mswServer.use(noDataSources);
    const view = renderWithProviders(<NewProjectPage />);
    const token = () => screen.getByPlaceholderText('Required only for private repositories');

    expect(token()).toBeDisabled();
    expect(await screen.findByText(/only an admin or a securityadmin sets a repository access token/i)).toBeInTheDocument();
    view.unmount();

    mswServer.use(permissionsHandler({ permissions: ['SecurityAdmin'] }));
    renderWithProviders(<NewProjectPage />);
    await waitFor(() => expect(token()).toBeEnabled());
  });

  it('says so, neutrally, when the project came back open', async () => {
    renderAndCreate(false);

    expect(await screen.findByText('project page')).toBeInTheDocument();
    expect(toast.success).toHaveBeenCalledWith(
      'Project created',
      expect.objectContaining({ description: expect.stringMatching(/^It is open to everyone with a Beacon role/) }),
    );
    expect(toast.error).not.toHaveBeenCalled();
  });
});
