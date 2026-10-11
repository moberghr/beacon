import { beforeEach, describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { Route, Routes } from 'react-router-dom';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { toast } from 'sonner';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { DashboardPermissionLevel } from '@/lib/enums';
import DashboardBuilderPage from './DashboardBuilderPage';
import DashboardViewerPage from './DashboardViewerPage';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn() },
}));

function dashboard(overrides: Record<string, unknown> = {}) {
  return {
    id: 4,
    name: 'Revenue',
    description: 'Daily revenue',
    isShared: false,
    isDefault: false,
    refreshIntervalSeconds: 60,
    createdTime: '2026-10-01T10:00:00Z',
    createdByUserName: 'Ada Lovelace',
    userPermissionLevel: DashboardPermissionLevel.Admin,
    widgets: [],
    ...overrides,
  };
}

function renderBuilder() {
  renderWithProviders(
    <Routes>
      <Route path="/dashboards/:id/edit" element={<DashboardBuilderPage />} />
    </Routes>,
    { initialEntries: ['/dashboards/4/edit'] },
  );
}

describe('DashboardBuilderPage sharing', () => {
  beforeEach(() => {
    vi.mocked(toast.success).mockClear();
    vi.mocked(toast.error).mockClear();
  });

  it("lets the dashboard's Admin level share it, sending its other settings unchanged", async () => {
    let body: unknown = null;
    mswServer.use(
      http.get('*/beacon/api/dashboards/4', () => HttpResponse.json(dashboard())),
      http.put('*/beacon/api/dashboards/4', async ({ request }) => {
        body = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderBuilder();

    fireEvent.click(await screen.findByRole('radio', { name: 'Shared' }));

    await waitFor(() =>
      expect(body).toEqual({
        name: 'Revenue',
        description: 'Daily revenue',
        isShared: true,
        isDefault: false,
        refreshIntervalSeconds: 60,
        layoutConfiguration: null,
      }),
    );
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Sharing updated'));
    expect(screen.getByRole('radiogroup', { name: 'Default dashboard' })).toBeInTheDocument();
  });

  it('shows an Edit grantee the sharing state without the toggles', async () => {
    mswServer.use(
      http.get('*/beacon/api/dashboards/4', () =>
        HttpResponse.json(dashboard({ isShared: true, userPermissionLevel: DashboardPermissionLevel.Edit })),
      ),
    );

    renderBuilder();

    expect(await screen.findByText(/only the dashboard's owner or an admin grantee/i)).toBeInTheDocument();
    expect(screen.getByText('Shared')).toBeInTheDocument();
    expect(screen.queryByRole('radiogroup', { name: 'Visibility' })).not.toBeInTheDocument();
    expect(screen.queryByRole('radiogroup', { name: 'Default dashboard' })).not.toBeInTheDocument();
  });

  it('says which level the change needs when the server refuses it (403)', async () => {
    mswServer.use(
      http.get('*/beacon/api/dashboards/4', () => HttpResponse.json(dashboard())),
      http.put('*/beacon/api/dashboards/4', () =>
        HttpResponse.json({ title: 'Forbidden', status: 403 }, { status: 403 }),
      ),
    );

    renderBuilder();

    fireEvent.click(await screen.findByRole('radio', { name: 'Default' }));

    await waitFor(() =>
      expect(toast.error).toHaveBeenCalledWith(
        "Only the dashboard's owner or an admin grantee can share it or make it the default.",
      ),
    );
    expect(toast.success).not.toHaveBeenCalled();
  });
});

describe('DashboardViewerPage owner', () => {
  function renderViewer(payload: Record<string, unknown>) {
    mswServer.use(http.get('*/beacon/api/dashboards/4', () => HttpResponse.json(payload)));
    renderWithProviders(
      <Routes>
        <Route path="/dashboards/:id" element={<DashboardViewerPage />} />
      </Routes>,
      { initialEntries: ['/dashboards/4'] },
    );
  }

  it("names the dashboard's owner", async () => {
    renderViewer(dashboard());
    expect(await screen.findByText(/by Ada Lovelace/)).toBeInTheDocument();
  });

  it('leaves the owner out when it has no name', async () => {
    renderViewer(dashboard({ createdByUserName: null }));
    expect(await screen.findByText(/daily revenue/i)).toBeInTheDocument();
    expect(screen.queryByText(/ by /)).not.toBeInTheDocument();
  });
});
