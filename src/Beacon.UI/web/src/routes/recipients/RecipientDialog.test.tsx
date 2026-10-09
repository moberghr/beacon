import { describe, it, expect, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { RecipientDialog } from './RecipientDialog';

describe('RecipientDialog', () => {
  it('POSTs the new recipient and calls onClose on success', async () => {
    let captured: unknown = null;
    mswServer.use(
      http.post('*/beacon/api/recipients', async ({ request }) => {
        captured = await request.json();
        return HttpResponse.json({ id: 42 });
      }),
    );

    const onClose = vi.fn();
    renderWithProviders(<RecipientDialog open onClose={onClose} />);

    fireEvent.input(screen.getByLabelText(/^Name/), { target: { value: 'Ops Team' } });
    fireEvent.input(screen.getByLabelText(/Email address/), { target: { value: 'ops@example.com' } });

    fireEvent.click(screen.getByRole('button', { name: /create recipient/i }));

    await waitFor(() => {
      expect(onClose).toHaveBeenCalled();
    });

    expect(captured).toMatchObject({
      name: 'Ops Team',
      destination: 'ops@example.com',
      notificationType: 2, // Email
    });
  });

  it('sends masked values back unchanged on edit, so the server keeps the stored secrets', async () => {
    let captured: unknown = null;
    mswServer.use(
      http.put('*/beacon/api/recipients/7', async ({ request }) => {
        captured = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );

    const onClose = vi.fn();
    renderWithProviders(
      <RecipientDialog
        open
        onClose={onClose}
        recipient={{
          id: 7,
          name: 'Ops hook',
          description: null,
          destination: 'https://hooks.example.com/********',
          notificationType: 5, // Webhook
          headersJson: '{"Authorization":"********"}',
          bodyTemplate: null,
          subscriptionCount: 1,
        }}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /save changes/i }));

    await waitFor(() => {
      expect(onClose).toHaveBeenCalled();
    });

    expect(captured).toMatchObject({
      destination: 'https://hooks.example.com/********',
      headersJson: '{"Authorization":"********"}',
    });
  });

  it('sends an empty headers object when stored headers are cleared on edit', async () => {
    let captured: { headersJson?: unknown } | null = null;
    mswServer.use(
      http.put('*/beacon/api/recipients/7', async ({ request }) => {
        captured = (await request.json()) as { headersJson?: unknown };
        return new HttpResponse(null, { status: 204 });
      }),
    );

    const onClose = vi.fn();
    renderWithProviders(
      <RecipientDialog
        open
        onClose={onClose}
        recipient={{
          id: 7,
          name: 'Ops hook',
          description: null,
          destination: 'https://hooks.example.com/********',
          notificationType: 5, // Webhook
          headersJson: '{"Authorization":"********"}',
          bodyTemplate: null,
          subscriptionCount: 1,
        }}
      />,
    );

    fireEvent.input(screen.getByLabelText(/Custom headers/), { target: { value: '' } });
    fireEvent.click(screen.getByRole('button', { name: /save changes/i }));

    await waitFor(() => {
      expect(onClose).toHaveBeenCalled();
    });

    expect(captured!.headersJson).toBe('{}');
  });

  it('sends no headers when a webhook is changed to another type', async () => {
    let captured: { headersJson?: unknown; notificationType?: unknown } | null = null;
    mswServer.use(
      http.put('*/beacon/api/recipients/7', async ({ request }) => {
        captured = (await request.json()) as { headersJson?: unknown; notificationType?: unknown };
        return new HttpResponse(null, { status: 204 });
      }),
    );

    const onClose = vi.fn();
    renderWithProviders(
      <RecipientDialog
        open
        onClose={onClose}
        recipient={{
          id: 7,
          name: 'Ops hook',
          description: null,
          destination: 'https://hooks.example.com/********',
          notificationType: 5, // Webhook
          headersJson: '{"Authorization":"********"}',
          bodyTemplate: null,
          subscriptionCount: 1,
        }}
      />,
    );

    fireEvent.change(screen.getByLabelText(/Notification type/), { target: { value: '4' } }); // Slack
    fireEvent.input(screen.getByLabelText(/Slack webhook URL/), { target: { value: 'https://hooks.slack.com/services/T0/B0/x' } });
    fireEvent.click(screen.getByRole('button', { name: /save changes/i }));

    await waitFor(() => {
      expect(onClose).toHaveBeenCalled();
    });

    expect(captured!.notificationType).toBe(4);
    expect(captured!.headersJson).toBeNull();
  });

  it('asks for the destination again when the stored one cannot be read', async () => {
    let captured: { destination?: unknown; headersJson?: unknown } | null = null;
    mswServer.use(
      http.put('*/beacon/api/recipients/7', async ({ request }) => {
        captured = (await request.json()) as { destination?: unknown; headersJson?: unknown };
        return new HttpResponse(null, { status: 204 });
      }),
    );

    const onClose = vi.fn();
    renderWithProviders(
      <RecipientDialog
        open
        onClose={onClose}
        recipient={{
          id: 7,
          name: 'Ops hook',
          description: null,
          destination: '********',
          notificationType: 5, // Webhook
          headersJson: null,
          bodyTemplate: null,
          secretsUnreadable: true,
          subscriptionCount: 1,
        }}
      />,
    );

    expect(screen.getByText(/stored destination cannot be read/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/Webhook URL/)).toHaveValue('');

    fireEvent.click(screen.getByRole('button', { name: /save changes/i }));
    await waitFor(() => {
      expect(screen.getByText(/Destination is required/i)).toBeInTheDocument();
    });

    fireEvent.input(screen.getByLabelText(/Webhook URL/), { target: { value: 'https://hooks.example.com/new' } });
    fireEvent.click(screen.getByRole('button', { name: /save changes/i }));
    await waitFor(() => {
      expect(onClose).toHaveBeenCalled();
    });

    expect(captured!.destination).toBe('https://hooks.example.com/new');
    expect(captured!.headersJson).toBe('{}');
  });

  it('shows a validation error when required fields are missing', async () => {
    const onClose = vi.fn();
    renderWithProviders(<RecipientDialog open onClose={onClose} />);

    fireEvent.click(screen.getByRole('button', { name: /create recipient/i }));

    await waitFor(() => {
      expect(screen.getByText(/Name is required/i)).toBeInTheDocument();
    });

    expect(onClose).not.toHaveBeenCalled();
  });
});
