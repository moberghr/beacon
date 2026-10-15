import { beforeEach, describe, it, expect, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { Route, Routes } from 'react-router-dom';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { toast } from 'sonner';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { permissionsHandler } from '@/test/permissions';
import { AccessReady } from '@/test/AccessReady';
import DataSourceDetailPage from './DataSourceDetailPage';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn(), warning: vi.fn() },
}));

const TARGET_CHANGE =
  'The new connection points at another server, port, database or engine. Confirm the target change to save it: the column values sampled from the old target are removed.';

function entry(overrides: Record<string, unknown> = {}) {
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
    disabled: false,
    transportVerified: true,
    editSettings: {
      hostManaged: false,
      metadataMaxTables: 250,
      metadataMaxColumnsPerTable: 80,
      metadataLoadTableNamesOnly: true,
      metadataExcludeSchemas: 'audit,tmp',
      metadataIncludeSchemas: null,
    },
    ...overrides,
  };
}

// The page's catch-all answers every other GET with 404: who the user is must be answered first.
const me = http.get('*/beacon/api/auth/me', () =>
  HttpResponse.json({ userId: 'u1', userName: 'tester', displayName: 'Tester', email: null, isAuthenticated: true, roles: [] }),
);
const dataSourceAdmin = permissionsHandler({ permissions: ['DataSourceAdmin'] });

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

function testConnectionAnswers(success: boolean, message: string) {
  return http.post('*/beacon/api/data-sources/test-connection', () => HttpResponse.json({ success, message }));
}

async function openDialog() {
  fireEvent.click(await screen.findByRole('button', { name: 'Replace connection' }));
  return screen.findByRole('dialog');
}

describe('Replace connection', () => {
  beforeEach(() => {
    vi.mocked(toast.success).mockClear();
    vi.mocked(toast.error).mockClear();
  });

  it('keeps Save off until the new connection passes the test, and never shows the stored connection', async () => {
    const tests: unknown[] = [];
    renderWith(
      entry(),
      me,
      dataSourceAdmin,
      http.post('*/beacon/api/data-sources/test-connection', async ({ request }) => {
        tests.push(await request.json());
        return HttpResponse.json({ success: true, message: 'Connected.' });
      }),
    );

    const dialog = await openDialog();
    const input = within(dialog).getByLabelText('New connection string') as HTMLInputElement;
    expect(input.value).toBe('');
    expect(input.type).toBe('password');
    expect(within(dialog).getByRole('button', { name: 'Save' })).toBeDisabled();
    expect(within(dialog).getByRole('button', { name: 'Test connection' })).toBeDisabled();

    fireEvent.change(input, { target: { value: 'Host=db2;SSL Mode=VerifyFull' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Test connection' }));

    expect(await within(dialog).findByText('Connected')).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Save' })).toBeEnabled();
    expect(tests).toEqual([
      { name: 'ledger-db', dataSourceType: 1, databaseEngineType: 1, connectionString: 'Host=db2;SSL Mode=VerifyFull' },
    ]);

    // Changing the connection after a passing test needs another test.
    fireEvent.change(input, { target: { value: 'Host=db3;SSL Mode=VerifyFull' } });
    expect(within(dialog).getByRole('button', { name: 'Save' })).toBeDisabled();
  });

  it('keeps Save off when the test fails and shows the reason', async () => {
    renderWith(entry(), me, dataSourceAdmin, testConnectionAnswers(false, 'Connection refused: SSL Mode must be VerifyFull.'));

    const dialog = await openDialog();
    fireEvent.change(within(dialog).getByLabelText('New connection string'), { target: { value: 'Host=db2' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Test connection' }));

    expect(await within(dialog).findByText('Connection refused: SSL Mode must be VerifyFull.')).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Save' })).toBeDisabled();
  });

  it('sends no metadata settings (so none is reset), asks to confirm a target change (409) and saves again with the confirmation', async () => {
    const saves: Record<string, unknown>[] = [];
    renderWith(
      entry(),
      me,
      dataSourceAdmin,
      testConnectionAnswers(true, 'Connected.'),
      http.put('*/beacon/api/data-sources/42', async ({ request }) => {
        const body = (await request.json()) as Record<string, unknown>;
        saves.push(body);
        return body.confirmTargetChange
          ? new HttpResponse(null, { status: 204 })
          : HttpResponse.json({ title: 'Conflict', status: 409, detail: TARGET_CHANGE }, { status: 409 });
      }),
    );

    const dialog = await openDialog();
    fireEvent.change(within(dialog).getByLabelText('Database engine'), { target: { value: '2' } });
    fireEvent.change(within(dialog).getByLabelText('New connection string'), { target: { value: 'Server=other;Encrypt=Strict' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Test connection' }));
    await within(dialog).findByText('Connected');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    expect(await within(dialog).findByText(TARGET_CHANGE)).toBeInTheDocument();
    const save = within(dialog).getByRole('button', { name: 'Save' });
    expect(save).toBeDisabled();
    fireEvent.click(within(dialog).getByLabelText('Confirm the target change'));
    expect(save).toBeEnabled();
    fireEvent.click(save);

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("Replaced the connection of 'ledger-db'."));
    expect(saves).toEqual([
      {
        name: 'ledger-db',
        databaseEngineType: 2,
        connectionString: 'Server=other;Encrypt=Strict',
        confirmTargetChange: false,
      },
      expect.objectContaining({ connectionString: 'Server=other;Encrypt=Strict', confirmTargetChange: true }),
    ]);
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  it('shows a refusal (400) as the server words it', async () => {
    const refusal = 'The connection does not verify the server: set SSL Mode to VerifyFull.';
    renderWith(
      entry(),
      me,
      dataSourceAdmin,
      testConnectionAnswers(true, 'Connected.'),
      http.put('*/beacon/api/data-sources/42', () =>
        HttpResponse.json({ title: 'Bad request', status: 400, detail: refusal }, { status: 400 })),
    );

    const dialog = await openDialog();
    fireEvent.change(within(dialog).getByLabelText('New connection string'), { target: { value: 'Host=db2' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Test connection' }));
    await within(dialog).findByText('Connected');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    expect(await within(dialog).findByText(refusal)).toBeInTheDocument();
    expect(screen.queryByLabelText('Confirm the target change')).not.toBeInTheDocument();
    expect(toast.success).not.toHaveBeenCalled();
  });
});

describe('Replace connection dialog state', () => {
  it('forgets the typed connection and the test when cancelled and reopened', async () => {
    renderWith(entry(), me, dataSourceAdmin, testConnectionAnswers(true, 'Connected.'));

    let dialog = await openDialog();
    fireEvent.change(within(dialog).getByLabelText('New connection string'), { target: { value: 'Host=db2' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Test connection' }));
    await within(dialog).findByText('Connected');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());

    dialog = await openDialog();
    expect((within(dialog).getByLabelText('New connection string') as HTMLInputElement).value).toBe('');
    expect(within(dialog).queryByText('Connected')).not.toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Save' })).toBeDisabled();
  });

  it('needs another test after the engine changes', async () => {
    renderWith(entry(), me, dataSourceAdmin, testConnectionAnswers(true, 'Connected.'));

    const dialog = await openDialog();
    fireEvent.change(within(dialog).getByLabelText('New connection string'), { target: { value: 'Host=db2' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Test connection' }));
    await within(dialog).findByText('Connected');
    expect(within(dialog).getByRole('button', { name: 'Save' })).toBeEnabled();

    fireEvent.change(within(dialog).getByLabelText('Database engine'), { target: { value: '2' } });
    expect(within(dialog).getByRole('button', { name: 'Save' })).toBeDisabled();
    expect(within(dialog).queryByText('Connected')).not.toBeInTheDocument();
  });

  it('takes connection data in a text area for a source that is not a database', async () => {
    renderWith(entry({ dataSourceType: 'Prometheus', databaseEngineType: null }), me, dataSourceAdmin);

    const dialog = await openDialog();
    const area = within(dialog).getByLabelText('New connection data') as HTMLTextAreaElement;
    expect(area.tagName).toBe('TEXTAREA');
    expect(area.value).toBe('');
    expect(within(dialog).queryByLabelText('Database engine')).not.toBeInTheDocument();
  });

  it('shows a 409 that is not the target-change message as an error, not a confirmation', async () => {
    renderWith(
      entry(),
      me,
      dataSourceAdmin,
      testConnectionAnswers(true, 'Connected.'),
      http.put('*/beacon/api/data-sources/42', () =>
        HttpResponse.json({ title: 'Conflict', status: 409, detail: 'Another edit won.' }, { status: 409 })),
    );

    const dialog = await openDialog();
    fireEvent.change(within(dialog).getByLabelText('New connection string'), { target: { value: 'Host=db2' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Test connection' }));
    await within(dialog).findByText('Connected');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    expect(await within(dialog).findByText('Another edit won.')).toBeInTheDocument();
    expect(within(dialog).queryByLabelText('Confirm the target change')).not.toBeInTheDocument();
  });

  it('shows a 409 after the confirmation was ticked as an error', async () => {
    renderWith(
      entry(),
      me,
      dataSourceAdmin,
      testConnectionAnswers(true, 'Connected.'),
      http.put('*/beacon/api/data-sources/42', async ({ request }) => {
        const body = (await request.json()) as Record<string, unknown>;
        return HttpResponse.json(
          { title: 'Conflict', status: 409, detail: body.confirmTargetChange ? 'The data source changed meanwhile.' : TARGET_CHANGE },
          { status: 409 },
        );
      }),
    );

    const dialog = await openDialog();
    fireEvent.change(within(dialog).getByLabelText('New connection string'), { target: { value: 'Host=db2' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Test connection' }));
    await within(dialog).findByText('Connected');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));
    fireEvent.click(await within(dialog).findByLabelText('Confirm the target change'));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    expect(await within(dialog).findByText('The data source changed meanwhile.')).toBeInTheDocument();
    expect(toast.success).not.toHaveBeenCalled();
  });
});

describe('Enable / disable', () => {
  beforeEach(() => {
    vi.mocked(toast.success).mockClear();
  });

  it('disables only after a confirmation that says every run, query and MCP call fails', async () => {
    const bodies: unknown[] = [];
    renderWith(
      entry(),
      me,
      dataSourceAdmin,
      http.put('*/beacon/api/data-sources/42/enabled', async ({ request }) => {
        bodies.push(await request.json());
        return new HttpResponse(null, { status: 204 });
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Disable' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/every query, preview, subscription run, data-quality check, MCP call/)).toBeInTheDocument();
    expect(bodies).toEqual([]);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Disable' }));

    await waitFor(() => expect(bodies).toEqual([{ enabled: false }]));
    expect(toast.success).toHaveBeenCalledWith("Disabled data source 'ledger-db'.");
  });

  it('sends nothing when the disable confirmation is cancelled', async () => {
    const bodies: unknown[] = [];
    renderWith(
      entry(),
      me,
      dataSourceAdmin,
      http.put('*/beacon/api/data-sources/42/enabled', async ({ request }) => {
        bodies.push(await request.json());
        return new HttpResponse(null, { status: 204 });
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Disable' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(bodies).toEqual([]);
  });

  it('keeps the confirmation open when disabling fails', async () => {
    renderWith(
      entry(),
      me,
      dataSourceAdmin,
      http.put('*/beacon/api/data-sources/42/enabled', () =>
        HttpResponse.json({ title: 'Bad request', status: 400, detail: 'Host-managed.' }, { status: 400 })),
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Disable' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Disable' }));

    await waitFor(() => expect(toast.error).toHaveBeenCalled());
    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(toast.success).not.toHaveBeenCalled();
  });

  it('enables a disabled source at once, and says it is disabled', async () => {
    const bodies: unknown[] = [];
    renderWith(
      entry({ disabled: true, disabledTime: '2026-10-15T10:00:00Z' }),
      me,
      dataSourceAdmin,
      http.put('*/beacon/api/data-sources/42/enabled', async ({ request }) => {
        bodies.push(await request.json());
        return new HttpResponse(null, { status: 204 });
      }),
    );

    expect(await screen.findByText(/Beacon makes no connection with it/)).toBeInTheDocument();
    fireEvent.click(await screen.findByRole('button', { name: 'Enable' }));

    await waitFor(() => expect(bodies).toEqual([{ enabled: true }]));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });
});

describe('Data source state badges on the detail page', () => {
  it('badges a connection whose transport is not verified, and a disabled source', async () => {
    renderWith(entry({ transportVerified: false, disabled: true }));

    const status = (await screen.findByText('Status', { selector: 'dt' })).nextElementSibling as HTMLElement;
    expect(within(status).getByText('Disabled')).toBeInTheDocument();
    expect(within(status).queryByText('Enabled')).not.toBeInTheDocument();
    expect(screen.getByText(/Beacon makes no connection with it/)).toBeInTheDocument();
    const read = (screen.getByText('Read-only', { selector: 'dt' })).nextElementSibling as HTMLElement;
    expect(within(read).getByText('Transport not verified')).toBeInTheDocument();
  });

  it('shows no transport badge when the transport is verified or unknown', async () => {
    renderWith(entry({ transportVerified: null }));

    expect(await screen.findByText('ledger-db', { selector: 'dd' })).toBeInTheDocument();
    expect(screen.queryByText('Transport not verified')).not.toBeInTheDocument();
  });
});

describe('Who sees the controls', () => {
  it('offers nothing to a user without DataSourceAdmin', async () => {
    renderWith(
      entry(),
      me,
      permissionsHandler({ permissions: ['Approver', 'Auditor', 'SecurityAdmin', 'UserAdmin', 'MigrationOperator'] }),
    );

    await screen.findByTestId('access-ready');
    const status = (await screen.findByText('Status', { selector: 'dt' })).nextElementSibling as HTMLElement;
    expect(within(status).getByText('Enabled')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Replace connection' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Disable' })).not.toBeInTheDocument();
  });

  it('offers nothing when the detail carries no edit settings', async () => {
    renderWith(entry({ editSettings: null }), me, dataSourceAdmin);

    expect(await screen.findByRole('button', { name: 'Delete' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Replace connection' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Disable' })).not.toBeInTheDocument();
  });

  it('offers nothing on a source the host application manages', async () => {
    renderWith(entry({ editSettings: { ...entry().editSettings, hostManaged: true } }), me, dataSourceAdmin);

    expect(await screen.findByRole('button', { name: 'Delete' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Replace connection' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Disable' })).not.toBeInTheDocument();
  });
});

describe('Connection tab', () => {
  it('says whether the server is verified over TLS', async () => {
    renderWith(entry({ transportVerified: true }));

    fireEvent.click(await screen.findByRole('tab', { name: /Connection/ }));
    expect(await screen.findByText('Server verified over TLS')).toBeInTheDocument();
  });

  it('badges an unverified transport', async () => {
    renderWith(entry({ transportVerified: false }));

    fireEvent.click(await screen.findByRole('tab', { name: /Connection/ }));
    const row = (await screen.findByText('Transport', { selector: 'dt' })).nextElementSibling as HTMLElement;
    expect(within(row).getByText('Transport not verified')).toBeInTheDocument();
  });
});
