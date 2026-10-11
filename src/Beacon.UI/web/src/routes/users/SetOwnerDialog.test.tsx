import { screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '@/test/render';
import { SetOwnerDialog } from './SetOwnerDialog';

describe('SetOwnerDialog', () => {
  it('says what the owner means for the resource when the page says so', () => {
    renderWithProviders(
      <SetOwnerDialog
        open
        noun="data contract"
        description="Scheduled evaluations run as the owner."
        busy={false}
        onClose={vi.fn()}
        onSubmit={vi.fn()}
      />,
    );

    expect(screen.getByRole('dialog', { name: "Change the data contract's owner" })).toBeInTheDocument();
    expect(screen.getByText('Scheduled evaluations run as the owner.')).toBeInTheDocument();
    expect(screen.queryByText(/may change, run and delete/)).not.toBeInTheDocument();
  });

  it('falls back to who may change, run and delete the resource', () => {
    renderWithProviders(<SetOwnerDialog open noun="AI actor" busy={false} onClose={vi.fn()} onSubmit={vi.fn()} />);

    expect(screen.getByText('The owner may change, run and delete this AI actor. Admins always can.')).toBeInTheDocument();
  });
});
