import { beforeEach, describe, it, expect, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { toast } from 'sonner';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { ReplaceConnectionDialog } from './ReplaceConnectionDialog';
import type { DataSourceEntry } from './queries';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn() },
}));

const entry = {
  id: 42,
  name: 'ledger-db',
  dataSourceType: 'Database',
  databaseEngineType: 'PostgreSQL',
} as DataSourceEntry;

const approval = { requestId: 77, kind: 4, requestedTime: '2026-10-23T08:00:00Z', awaitsFirstApproval: false };

function serve(afterSave: Record<string, unknown>) {
  mswServer.use(
    http.get('*/beacon/api/csrf', () => HttpResponse.json({ token: 'test-token' })),
    http.post('*/beacon/api/data-sources/test-connection', () => HttpResponse.json({ success: true, message: 'Connected.' })),
    http.put('*/beacon/api/data-sources/42', () => new HttpResponse(null, { status: 204 })),
    http.get('*/beacon/api/data-sources/42', () => HttpResponse.json({ ...entry, ...afterSave })),
  );
}

async function replaceConnection(onClose = vi.fn(), shown: DataSourceEntry = entry) {
  renderWithProviders(<ReplaceConnectionDialog entry={shown} open onClose={onClose} />);
  fireEvent.change(screen.getByLabelText('New connection string'), { target: { value: 'Host=db2;SSL Mode=VerifyFull' } });
  fireEvent.click(screen.getByRole('button', { name: 'Test connection' }));
  await screen.findByText('Connected');
  fireEvent.click(screen.getByRole('button', { name: 'Save' }));
  return onClose;
}

describe('ReplaceConnectionDialog approval', () => {
  beforeEach(() => {
    vi.mocked(toast.info).mockClear();
    vi.mocked(toast.success).mockClear();
  });

  it('notes what waits for approval and names the engines where a rotation waits too', () => {
    renderWithProviders(<ReplaceConnectionDialog entry={entry} open onClose={vi.fn()} />);

    expect(screen.getByText(/any change other than the password or another secret waits for a second person/)).toBeInTheDocument();
    expect(screen.getByText(/for BigQuery, Databricks and CloudWatch connections every change waits, a rotation included/)).toBeInTheDocument();
    expect(screen.queryByText(/A password-only change applies at once\./)).not.toBeInTheDocument();
  });

  it('says so when the saved connection was held for approval', async () => {
    serve({ approval });
    const onClose = await replaceConnection();

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(toast.info).toHaveBeenCalledWith(expect.stringContaining("Submitted for approval: the new connection of 'ledger-db'"));
    expect(toast.success).not.toHaveBeenCalled();
  });

  it('reports a replaced connection that applied at once as replaced', async () => {
    serve({ approval: null });
    const onClose = await replaceConnection();

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(toast.success).toHaveBeenCalledWith("Replaced the connection of 'ledger-db'.");
    expect(toast.info).not.toHaveBeenCalled();
  });

  it('says held when the save re-sent the proposal a pending request already holds (same request id and time)', async () => {
    serve({ approval });
    const onClose = await replaceConnection(vi.fn(), { ...entry, approval } as DataSourceEntry);

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(toast.info).toHaveBeenCalledWith(expect.stringContaining("Submitted for approval: the new connection of 'ledger-db'"));
    expect(toast.success).not.toHaveBeenCalled();
  });

  it('says held for a source that awaits its first approval with no request visible', async () => {
    serve({ approval: null, awaitsFirstApproval: true });
    const onClose = await replaceConnection();

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(toast.info).toHaveBeenCalledWith(expect.stringContaining("Submitted for approval: the new connection of 'ledger-db'"));
    expect(toast.success).not.toHaveBeenCalled();
  });

  it('claims neither replaced nor held when the data source cannot be re-read after the save', async () => {
    serve({});
    mswServer.use(http.get('*/beacon/api/data-sources/42', () => HttpResponse.json({ title: 'boom', status: 500 }, { status: 500 })));
    const onClose = await replaceConnection();

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(toast.success).not.toHaveBeenCalled();
    expect(toast.info).toHaveBeenCalledWith(expect.stringContaining('Could not check whether it applied or waits for approval'));
  });
});
