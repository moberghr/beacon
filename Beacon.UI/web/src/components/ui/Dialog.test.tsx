import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { Dialog } from './Dialog';

describe('Dialog accessibility', () => {
  it('renders as a labelled modal dialog', () => {
    render(
      <Dialog open onClose={() => {}} title="Settings">
        <button>Inside</button>
      </Dialog>,
    );
    const dialog = screen.getByRole('dialog', { name: 'Settings' });
    expect(dialog).toHaveAttribute('aria-modal', 'true');
  });

  it('closes on Escape', () => {
    const onClose = vi.fn();
    render(
      <Dialog open onClose={onClose} title="Settings">
        <button>Inside</button>
      </Dialog>,
    );
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('traps Tab focus within the dialog', () => {
    render(
      <Dialog open onClose={() => {}} title="Settings">
        <button>First</button>
        <button>Last</button>
      </Dialog>,
    );

    const first = screen.getByRole('button', { name: 'First' });
    const closeBtn = screen.getByRole('button', { name: 'Close dialog' });

    // Forward Tab from the last focusable wraps to the first.
    const focusables = screen.getAllByRole('button');
    const last = focusables[focusables.length - 1];
    last.focus();
    fireEvent.keyDown(document, { key: 'Tab' });
    expect(document.activeElement).toBe(closeBtn);

    // Shift+Tab from the first focusable wraps to the last.
    closeBtn.focus();
    fireEvent.keyDown(document, { key: 'Tab', shiftKey: true });
    expect(document.activeElement).toBe(last);

    // Sanity: first button is inside the dialog and reachable.
    expect(first).toBeInTheDocument();
  });
});
