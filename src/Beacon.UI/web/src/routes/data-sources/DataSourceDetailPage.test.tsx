import { afterEach, beforeEach, describe, it, expect, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { Route, Routes } from 'react-router-dom';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { toast } from 'sonner';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { permissionsHandler } from '@/test/permissions';
import { AccessReady } from '@/test/AccessReady';
import DataSourceDetailPage from './DataSourceDetailPage';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn(), warning: vi.fn() },
}));

function entry(overrides: Record<string, unknown>) {
  return {
    id: 42,
    name: 'ledger-db',
    dataSourceType: 'Database',
    databaseEngineType: 'PostgreSQL',
    queryCount: 0,
    migrationJobsCount: 0,
    metadataLoadingEnabled: true,
    openWhenUnlinked: true,
    projects: [],
    otherProjectCount: 0,
    ...overrides,
  };
}

function renderWith(payload: Record<string, unknown>, ...extra: Parameters<typeof mswServer.use>) {
  mswServer.use(
    ...extra,
    http.get('*/beacon/api/data-sources/42', () => HttpResponse.json(payload)),
    http.get('*/beacon/api/data-sources/42/metadata', () => HttpResponse.json({ tables: [] })),
    http.get('*/beacon/api/*', () => HttpResponse.json({ title: 'Not found', status: 404 }, { status: 404 })),
  );
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/data-sources/:id" element={<DataSourceDetailPage />} />
      </Routes>
      <AccessReady />
    </>,
    { initialEntries: ['/data-sources/42'] },
  );
}

describe('DataSourceDetailPage projects', () => {
  it('links the readable projects to their pages and counts the others without naming them', async () => {
    renderWith(entry({ projects: [{ id: 5, name: 'Ledger' }, { id: 6, name: 'Audit' }], otherProjectCount: 2 }));

    expect(await screen.findByRole('link', { name: 'Ledger' })).toHaveAttribute('href', '/projects/5');
    expect(screen.getByRole('link', { name: 'Audit' })).toHaveAttribute('href', '/projects/6');
    expect(screen.getByText('+2 other')).toBeInTheDocument();
  });

  it('marks a source no project links, and that is not open, as Admin-only', async () => {
    renderWith(entry({ openWhenUnlinked: false }));

    expect(await screen.findByText('Admins only')).toBeInTheDocument();
    expect(screen.queryByText('Not linked')).not.toBeInTheDocument();
  });
});

describe('DataSourceDetailPage read-only enforcement', () => {
  it('warns that a source whose engine does not refuse writes needs a read-only login', async () => {
    renderWith(entry({ databaseEngineType: 'MSSQL', databaseReadOnlyEnforced: false }));

    expect(await screen.findByText('No database-level read-only enforcement')).toBeInTheDocument();
    expect(screen.getByText(/login that can only read/)).toBeInTheDocument();
    expect(screen.getByText('Read-only login required')).toBeInTheDocument();
  });

  it('says the engine enforces read-only where it does, still asking for a read-only login, without a warning', async () => {
    renderWith(entry({ databaseReadOnlyEnforced: true }));

    expect(await screen.findByText('Enforced by the engine — still use a read-only login')).toHaveAttribute(
      'title',
      expect.stringMatching(/defence in depth.*login that can only read/),
    );
    expect(screen.queryByText('No database-level read-only enforcement')).not.toBeInTheDocument();
  });
});

describe('DataSourceDetailPage delete', () => {
  // The page's catch-all answers every other GET with 404: who the user is must be answered first.
  const me = http.get('*/beacon/api/auth/me', () =>
    HttpResponse.json({ userId: 'u1', userName: 'tester', displayName: 'Tester', email: null, isAuthenticated: true, roles: [] }),
  );

  it('is offered to a DataSourceAdmin', async () => {
    renderWith(entry({}), me, permissionsHandler({ permissions: ['DataSourceAdmin'] }));

    expect(await screen.findByRole('button', { name: 'Delete' })).toBeInTheDocument();
  });

  it('is not offered to a holder of every other permission', async () => {
    renderWith(
      entry({}),
      me,
      permissionsHandler({ permissions: ['Approver', 'Auditor', 'SecurityAdmin', 'UserAdmin', 'MigrationOperator'] }),
    );

    expect(await screen.findByRole('button', { name: /refresh metadata/i })).toBeInTheDocument();
    await screen.findByTestId('access-ready');
    expect(screen.queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument();
  });
});

describe('DataSourceDetailPage metadata refresh', () => {
  beforeEach(() => {
    vi.mocked(toast.warning).mockClear();
    vi.mocked(toast.error).mockClear();
    vi.mocked(toast.success).mockClear();
  });

  // A refresh's toasts are not left for the next describe block to find.
  afterEach(() => {
    vi.mocked(toast.warning).mockClear();
    vi.mocked(toast.error).mockClear();
    vi.mocked(toast.success).mockClear();
  });

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

  const started = http.post('*/beacon/api/data-sources/42/metadata-refreshes', () =>
    HttpResponse.json({ taskId: 31, jobId: 'job-1', state: 0 }, { status: 202 }),
  );

  it('says when the metadata was refreshed too recently (429), as a warning rather than a failure', async () => {
    const cooldown = "This data source's metadata was refreshed less than 15 minutes ago; try again later.";
    renderWith(
      entry({}),
      http.post('*/beacon/api/data-sources/42/metadata-refreshes', () =>
        HttpResponse.json({ title: cooldown, status: 429 }, { status: 429 }),
      ),
    );

    fireEvent.click(await screen.findByRole('button', { name: /refresh metadata/i }));

    await waitFor(() => expect(toast.warning).toHaveBeenCalledWith(cooldown));
    expect(toast.error).not.toHaveBeenCalled();
  });

  it("reports any other refusal as a failure with the server's reason", async () => {
    renderWith(
      entry({}),
      http.post('*/beacon/api/data-sources/42/metadata-refreshes', () =>
        HttpResponse.json({ title: 'Metadata loading is disabled for this data source.', status: 400 }, { status: 400 }),
      ),
    );

    fireEvent.click(await screen.findByRole('button', { name: /refresh metadata/i }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith('Metadata loading is disabled for this data source.'));
  });

  it('starts a background refresh, shows its progress, and reads the catalog again when it succeeds', async () => {
    let reads = 0;
    let metadataReads = 0;
    renderWith(
      entry({}),
      started,
      http.get('*/beacon/api/data-sources/42/metadata-refreshes/31', () => {
        reads += 1;
        return HttpResponse.json(reads === 1 ? task(1) : task(2, { warningCount: 0 }));
      }),
      http.get('*/beacon/api/data-sources/42/metadata', () => {
        metadataReads += 1;
        return HttpResponse.json({ tables: [] });
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: /refresh metadata/i }));

    expect(await screen.findByRole('button', { name: /refreshing metadata/i })).toBeDisabled();
    const before = metadataReads;
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Metadata refreshed from source'), { timeout: 5000 });
    await waitFor(() => expect(metadataReads).toBeGreaterThan(before));
    expect(await screen.findByRole('button', { name: /refresh metadata/i })).toBeEnabled();
  });

  it('says how many names the refresh left out', async () => {
    renderWith(
      entry({}),
      started,
      http.get('*/beacon/api/data-sources/42/metadata-refreshes/31', () => HttpResponse.json(task(2, { warningCount: 3 }))),
    );

    fireEvent.click(await screen.findByRole('button', { name: /refresh metadata/i }));

    await waitFor(() => expect(toast.warning).toHaveBeenCalledWith(expect.stringContaining('3 table or column names were left out')));
  });

  it('says why a background refresh failed, by its code', async () => {
    renderWith(
      entry({}),
      started,
      http.get('*/beacon/api/data-sources/42/metadata-refreshes/31', () =>
        HttpResponse.json(task(3, { failureCode: 'data_source_disabled' })),
      ),
    );

    fireEvent.click(await screen.findByRole('button', { name: /refresh metadata/i }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith('This data source is disabled; enable it to refresh its metadata.'));
    expect(toast.success).not.toHaveBeenCalled();
  });
});

describe('DataSourceDetailPage migration write target', () => {
  const me = http.get('*/beacon/api/auth/me', () =>
    HttpResponse.json({ userId: 'u1', userName: 'tester', displayName: 'Tester', email: null, isAuthenticated: true, roles: [] }),
  );

  it('labels a write target as one, never read by Beacon, without the read-only login warning', async () => {
    renderWith(entry({ databaseEngineType: 'MSSQL', databaseReadOnlyEnforced: false, allowMigrationWrites: true }));

    expect(await screen.findByText('Not read by Beacon')).toBeInTheDocument();
    expect(screen.getAllByText('Migration write target').length).toBeGreaterThan(0);
    expect(screen.queryByText('No database-level read-only enforcement')).not.toBeInTheDocument();
    expect(screen.queryByText('Read-only login required')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /refresh metadata/i })).not.toBeInTheDocument();
  });

  it('lets an Admin mark a source as a write target, after confirming', async () => {
    let sent: unknown = null;
    renderWith(
      entry({}),
      me,
      permissionsHandler({ isAdmin: true }),
      http.put('*/beacon/api/data-sources/42/migration-writes', async ({ request }) => {
        sent = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Mark as migration write target' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Mark as write target' }));

    await waitFor(() => expect(sent).toEqual({ allowMigrationWrites: true }));
  });

  it('names what reads a source in use and marks it only once the Admin confirms again', async () => {
    const sent: unknown[] = [];
    const reason = 'Data source 42 is read by 2 queries and 1 subscription. Beacon stops reading a migration write target, so they will fail. Confirm to mark it anyway.';
    renderWith(
      entry({}),
      me,
      permissionsHandler({ isAdmin: true }),
      http.put('*/beacon/api/data-sources/42/migration-writes', async ({ request }) => {
        const body = (await request.json()) as { force?: boolean };
        sent.push(body);
        return body.force
          ? new HttpResponse(null, { status: 204 })
          : HttpResponse.json({ title: reason, status: 409 }, { status: 409 });
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Mark as migration write target' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Mark as write target' }));
    expect(await screen.findByText(reason)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Mark anyway' }));

    await waitFor(() => expect(sent).toEqual([{ allowMigrationWrites: true }, { allowMigrationWrites: true, force: true }]));
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Marked as a migration write target.'));
    expect(toast.error).not.toHaveBeenCalled();
  });

  it('leaves a source in use as it was when the Admin does not confirm again', async () => {
    const sent: unknown[] = [];
    renderWith(
      entry({}),
      me,
      permissionsHandler({ isAdmin: true }),
      http.put('*/beacon/api/data-sources/42/migration-writes', async ({ request }) => {
        sent.push(await request.json());
        return HttpResponse.json({ title: 'Data source 42 is read by 1 query.', status: 409 }, { status: 409 });
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Mark as migration write target' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Mark as write target' }));
    expect(await screen.findByText('Data source 42 is read by 1 query.')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    await waitFor(() => expect(screen.queryByText('Data source 42 is read by 1 query.')).not.toBeInTheDocument());
    expect(sent).toEqual([{ allowMigrationWrites: true }]);
  });

  it('shows why a change failed for any other reason', async () => {
    renderWith(
      entry({ allowMigrationWrites: true }),
      me,
      permissionsHandler({ isAdmin: true }),
      http.put('*/beacon/api/data-sources/42/migration-writes', () =>
        HttpResponse.json({ title: 'The data source changed meanwhile.', status: 409 }, { status: 409 }),
      ),
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Return to a read source' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Return to reads' }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith('The data source changed meanwhile.'));
    expect(screen.queryByRole('button', { name: 'Mark anyway' })).not.toBeInTheDocument();
  });

  it('offers the switch to no one but an Admin', async () => {
    renderWith(entry({}), me, permissionsHandler({ permissions: ['DataSourceAdmin', 'MigrationOperator'] }));

    expect(await screen.findByText('Migration writes')).toBeInTheDocument();
    await screen.findByTestId('access-ready');
    expect(screen.queryByRole('button', { name: 'Mark as migration write target' })).not.toBeInTheDocument();
  });

  it('offers an Admin no marking while migrations are off, but still the way back for a target', async () => {
    renderWith(entry({}), me, permissionsHandler({ isAdmin: true, migrationsEnabled: false }));

    expect(await screen.findByText('Migration writes')).toBeInTheDocument();
    await screen.findByTestId('access-ready');
    expect(screen.queryByRole('button', { name: 'Mark as migration write target' })).not.toBeInTheDocument();
  });

  it('lets an Admin return a write target to a read source while migrations are off', async () => {
    let sent: unknown = null;
    renderWith(
      entry({ allowMigrationWrites: true }),
      me,
      permissionsHandler({ isAdmin: true, migrationsEnabled: false }),
      http.put('*/beacon/api/data-sources/42/migration-writes', async ({ request }) => {
        sent = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Return to a read source' }));
    expect(sent).toBeNull();
    expect(await screen.findByText(/change its login to one that can only read first/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Return to reads' }));

    await waitFor(() => expect(sent).toEqual({ allowMigrationWrites: false }));
  });

  it('keeps a write target when the Admin cancels returning it to a read source', async () => {
    let sent: unknown = null;
    renderWith(
      entry({ allowMigrationWrites: true }),
      me,
      permissionsHandler({ isAdmin: true }),
      http.put('*/beacon/api/data-sources/42/migration-writes', async ({ request }) => {
        sent = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Return to a read source' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Cancel' }));

    await waitFor(() => expect(screen.queryByText(/change its login to one that can only read first/i)).not.toBeInTheDocument());
    expect(sent).toBeNull();
  });
});

describe('DataSourceDetailPage held connection', () => {
  const approval = { requestId: 41, kind: 4, requestedTime: '2026-10-23T08:00:00Z', awaitsFirstApproval: true };

  it('says a held source connects to nothing until approved and how to withdraw it, badging it', async () => {
    renderWith(
      entry({ approval }),
      http.get('*/beacon/api/auth/me', () =>
        HttpResponse.json({ userId: 'u1', userName: 'tester', displayName: 'Tester', email: null, isAuthenticated: true, roles: [] })),
      permissionsHandler({ permissions: ['Approver'] }),
    );

    expect(await screen.findByText('Held for approval')).toBeInTheDocument();
    expect(screen.getByText(/connects to nothing until a second person approves its connection/)).toBeInTheDocument();
    expect(screen.getByText(/delete the data source, or save the connection that was approved before back/)).toBeInTheDocument();
    expect(screen.getByText('Awaiting approval')).toBeInTheDocument();
    expect(await screen.findByRole('link', { name: 'Review the pending change' })).toHaveAttribute('href', '/approvals/changes/41');
  });

  it('shows neither banner nor badge when nothing waits', async () => {
    renderWith(entry({}));

    expect(await screen.findByText('Identifier')).toBeInTheDocument();
    expect(screen.queryByText('Held for approval')).not.toBeInTheDocument();
    expect(screen.queryByText('Awaiting approval')).not.toBeInTheDocument();
  });

  it('tells every reader a held source with no pending request must be saved again', async () => {
    renderWith(entry({ approval: null, awaitsFirstApproval: true }));

    expect(await screen.findByText('Held: no change request waits')).toBeInTheDocument();
    expect(screen.getByText(/Save the connection to submit it for approval again/)).toBeInTheDocument();
    expect(screen.getByText('Held')).toBeInTheDocument();
    expect(screen.queryByText('Held for approval')).not.toBeInTheDocument();
  });
});
