import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { CreateEmbedModal } from './CreateEmbedModal';

const csrf = http.get('*/beacon/api/csrf', () => HttpResponse.json({ token: 'test-token' }));

const queryList = http.get('*/beacon/api/queries', () =>
  HttpResponse.json({
    totalCount: 1,
    pageCount: 1,
    items: [{ queryId: 7, name: 'Orders by region', description: null, createdTime: '2026-10-01T09:00:00Z', steps: [] }],
  }),
);

function queryDetail(runnableVersion: number | null) {
  return http.get('*/beacon/api/queries/7', () =>
    HttpResponse.json({
      id: 7,
      name: 'Orders by region',
      steps: [
        { stepId: 1, stepOrder: 1, name: 'one', parameters: [{ name: 'region', type: 0, description: null, placeholder: '{region}' }] },
        { stepId: 2, stepOrder: 2, name: 'two', parameters: [{ name: 'region', type: 0, description: null, placeholder: '{region}' }, { name: 'year', type: 1, description: null, placeholder: '{year}' }] },
      ],
      mcpToolRunnableVersionNumber: runnableVersion,
      mcpToolIssue: null,
    }),
  );
}

/** Records every create request; answers with a secret shown once. */
function recordCreates() {
  const payloads: unknown[] = [];
  mswServer.use(
    csrf,
    queryList,
    queryDetail(3),
    http.post('*/beacon/api/embed-grants', async ({ request }) => {
      payloads.push(await request.json());
      return HttpResponse.json({
        id: 11,
        publicId: '0123456789abcdef0123456789abcdef',
        secret: 'bge_shown-once',
        queryVersionId: 30,
        versionNumber: 3,
        expiresTime: '2026-11-23T09:00:00Z',
        allowedOrigins: ['https://portal.example.com'],
        lockedParameters: ['region'],
      });
    }),
  );
  return payloads;
}

async function fillForm() {
  fireEvent.change(screen.getByPlaceholderText('e.g. Partner portal orders'), { target: { value: 'Partner orders' } });
  fireEvent.click(screen.getByRole('button', { name: 'Project' }));
  fireEvent.click(await screen.findByRole('option', { name: 'Acme Analytics' }));
  await screen.findByRole('option', { name: 'Orders by region' });
  fireEvent.change(screen.getByRole('combobox', { name: 'Query' }), { target: { value: '7' } });
  await screen.findByText('Version 3 is embedded. A newer approved version stops the grant.');
  fireEvent.change(screen.getByRole('textbox', { name: /Allowed origins/ }), {
    target: { value: 'https://portal.example.com' },
  });
  fireEvent.change(screen.getByRole('combobox', { name: 'year mode' }), { target: { value: 'fixed' } });
  fireEvent.change(screen.getByRole('textbox', { name: 'year value' }), { target: { value: '2026' } });
}

describe('CreateEmbedModal', () => {
  it('creates a grant with every parameter fixed or locked and shows its secret once', async () => {
    const payloads = recordCreates();
    renderWithProviders(<CreateEmbedModal open onClose={() => {}} />);

    await fillForm();
    expect(screen.getAllByText(/^region$|^year$/)).toHaveLength(2);
    fireEvent.click(screen.getByRole('button', { name: 'Create grant' }));

    expect(await screen.findByText('bge_shown-once')).toBeInTheDocument();
    expect(screen.getByText('This secret will not be shown again.')).toBeInTheDocument();
    expect(screen.getByText(/BeaconEmbedGrant \$BEACON_EMBED_SECRET/)).toBeInTheDocument();
    expect(screen.queryByText(/BeaconEmbedGrant bge_shown-once/)).not.toBeInTheDocument();
    expect(payloads).toEqual([
      {
        projectId: 1,
        queryId: 7,
        name: 'Partner orders',
        allowedOrigins: ['https://portal.example.com'],
        expiresInDays: 30,
        fixedParameters: { year: '2026' },
        lockedParameters: ['region'],
      },
    ]);
  });

  it('forgets the secret once the dialog closes', async () => {
    recordCreates();
    let open = true;
    const onClose = () => {
      open = false;
    };
    const view = renderWithProviders(<CreateEmbedModal open={open} onClose={onClose} />);

    await fillForm();
    fireEvent.click(screen.getByRole('button', { name: 'Create grant' }));
    await screen.findByText('bge_shown-once');
    fireEvent.click(screen.getByRole('button', { name: "Done — I've copied the secret" }));
    expect(open).toBe(false);

    view.rerender(<CreateEmbedModal open={false} onClose={onClose} />);
    view.rerender(<CreateEmbedModal open onClose={onClose} />);

    expect(await screen.findByText('New embed grant')).toBeInTheDocument();
    expect(screen.queryByText('bge_shown-once')).not.toBeInTheDocument();
  });

  it('refuses a query without an approved version before asking the server', async () => {
    const payloads: unknown[] = [];
    mswServer.use(
      csrf,
      queryList,
      queryDetail(null),
      http.post('*/beacon/api/embed-grants', async ({ request }) => {
        payloads.push(await request.json());
        return HttpResponse.json({});
      }),
    );
    renderWithProviders(<CreateEmbedModal open onClose={() => {}} />);

    fireEvent.change(screen.getByPlaceholderText('e.g. Partner portal orders'), { target: { value: 'Partner orders' } });
    fireEvent.click(screen.getByRole('button', { name: 'Project' }));
    fireEvent.click(await screen.findByRole('option', { name: 'Acme Analytics' }));
    await screen.findByRole('option', { name: 'Orders by region' });
    fireEvent.change(screen.getByRole('combobox', { name: 'Query' }), { target: { value: '7' } });

    expect(await screen.findByText(/only an approved version can be embedded/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Create grant' }));
    expect(await screen.findByText('Pick a query with an approved version.')).toBeInTheDocument();
    expect(payloads).toHaveLength(0);
  });

  it('cannot be closed while the create call is in flight', async () => {
    let release: () => void = () => {};
    const answered = new Promise<void>(x => {
      release = x;
    });
    mswServer.use(
      csrf,
      queryList,
      queryDetail(3),
      http.post('*/beacon/api/embed-grants', async () => {
        await answered;
        return HttpResponse.json({
          id: 11,
          publicId: '0123456789abcdef0123456789abcdef',
          secret: 'bge_shown-once',
          queryVersionId: 30,
          versionNumber: 3,
          expiresTime: '2026-11-23T09:00:00Z',
          allowedOrigins: ['https://portal.example.com'],
          lockedParameters: ['region'],
        });
      }),
    );
    const onClose = vi.fn();
    renderWithProviders(<CreateEmbedModal open onClose={onClose} />);

    await fillForm();
    fireEvent.click(screen.getByRole('button', { name: 'Create grant' }));
    await screen.findByRole('button', { name: 'Creating…' });

    fireEvent.keyDown(document, { key: 'Escape' });
    fireEvent.mouseDown(screen.getByRole('dialog').parentElement!);
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    fireEvent.click(screen.getByRole('button', { name: 'Close dialog' }));
    expect(onClose).not.toHaveBeenCalled();

    release();

    expect(await screen.findByText('bge_shown-once')).toBeInTheDocument();
    expect(onClose).not.toHaveBeenCalled();
  });

  it("shows the server's reason when it refuses the grant", async () => {
    mswServer.use(
      csrf,
      queryList,
      queryDetail(3),
      http.post('*/beacon/api/embed-grants', () =>
        HttpResponse.json(
          { title: 'Bad Request', status: 400, detail: 'The grant would render nothing: you cannot run this project\'s queries.' },
          { status: 400, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    );
    renderWithProviders(<CreateEmbedModal open onClose={() => {}} />);

    await fillForm();
    fireEvent.click(screen.getByRole('button', { name: 'Create grant' }));

    await waitFor(() => expect(screen.getByText(/would render nothing/)).toBeInTheDocument());
    expect(screen.queryByText('bge_shown-once')).not.toBeInTheDocument();
  });
});
