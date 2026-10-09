import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { renderWithProviders } from '@/test/render';
import { mswServer } from '../../../vitest.setup';
import SetupPage from './SetupPage';

function serveStatus(isFirstRun: boolean) {
  mswServer.use(
    http.get('*/beacon/api/setup/status', () => HttpResponse.json({ isFirstRun })),
    http.get('*/beacon/api/csrf', () => new HttpResponse(null, { status: 204 })),
  );
}

function fill(label: RegExp, value: string) {
  fireEvent.change(screen.getByLabelText(label), { target: { value } });
}

describe('SetupPage', () => {
  it('asks for the setup token while no user exists', async () => {
    serveStatus(true);

    renderWithProviders(<SetupPage />);

    expect(await screen.findByLabelText(/setup token/i)).toBeInTheDocument();
  });

  it('reports an installation that is already set up', async () => {
    serveStatus(false);

    renderWithProviders(<SetupPage />);

    expect(await screen.findByText(/has already been set up/i)).toBeInTheDocument();
    expect(screen.queryByLabelText(/setup token/i)).toBeNull();
  });

  it('masks the setup token like a password', async () => {
    serveStatus(true);

    renderWithProviders(<SetupPage />);

    expect(await screen.findByLabelText(/setup token/i)).toHaveAttribute('type', 'password');
  });

  it('requires the setup token before anything is sent', async () => {
    serveStatus(true);
    let posted = false;
    mswServer.use(
      http.post('*/beacon/api/setup/superadmin', () => {
        posted = true;
        return HttpResponse.json({ success: true });
      }),
    );

    renderWithProviders(<SetupPage />);
    await screen.findByLabelText(/setup token/i);
    fill(/username/i, 'admin');
    fill(/^password/i, 'Aa1!aaaa');
    fill(/confirm password/i, 'Aa1!aaaa');
    fireEvent.click(screen.getByRole('button', { name: /create super admin/i }));

    expect(await screen.findByText('Setup token is required')).toBeInTheDocument();
    expect(posted).toBe(false);
  });

  it('reports success once the super admin is created', async () => {
    serveStatus(true);
    mswServer.use(
      http.post('*/beacon/api/setup/superadmin', () =>
        HttpResponse.json({ success: true, userId: 1, message: 'Super admin created successfully.' }),
      ),
    );

    renderWithProviders(<SetupPage />);
    await screen.findByLabelText(/setup token/i);
    fill(/setup token/i, 'token-from-the-console');
    fill(/username/i, 'admin');
    fill(/^password/i, 'Aa1!aaaa');
    fill(/confirm password/i, 'Aa1!aaaa');
    fireEvent.click(screen.getByRole('button', { name: /create super admin/i }));

    expect(await screen.findByText(/super admin account created/i)).toBeInTheDocument();
    expect(screen.queryByLabelText(/setup token/i)).toBeNull();
  });

  it('sends the setup token and shows the server refusal', async () => {
    serveStatus(true);
    let sent: Record<string, unknown> | null = null;
    mswServer.use(
      http.post('*/beacon/api/setup/superadmin', async ({ request }) => {
        sent = (await request.json()) as Record<string, unknown>;
        return HttpResponse.json(
          { title: 'Forbidden', status: 403, detail: 'The setup token is missing or invalid.' },
          { status: 403, headers: { 'Content-Type': 'application/problem+json' } },
        );
      }),
    );

    renderWithProviders(<SetupPage />);
    await screen.findByLabelText(/setup token/i);
    fill(/setup token/i, 'token-from-the-console');
    fill(/username/i, 'admin');
    fill(/^password/i, 'Aa1!aaaa');
    fill(/confirm password/i, 'Aa1!aaaa');
    fireEvent.click(screen.getByRole('button', { name: /create super admin/i }));

    expect(await screen.findByText('The setup token is missing or invalid.')).toBeInTheDocument();
    await waitFor(() => expect(sent).not.toBeNull());
    expect(sent).toMatchObject({ setupToken: 'token-from-the-console', userName: 'admin' });
  });
});
