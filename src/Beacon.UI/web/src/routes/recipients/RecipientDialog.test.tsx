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
