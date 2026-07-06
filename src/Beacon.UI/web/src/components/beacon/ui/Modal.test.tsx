import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { Modal, ModalHeader, ModalBody } from './Modal';

describe('Modal', () => {
  it('renders via portal into document.body as a labelled modal dialog', () => {
    const { container } = render(
      <Modal open onClose={() => {}} ariaLabel="Example">
        <ModalBody>content</ModalBody>
      </Modal>,
    );

    const dialog = screen.getByRole('dialog', { name: 'Example' });
    expect(dialog).toHaveAttribute('aria-modal', 'true');
    // Portal: the dialog is NOT inside the render container.
    expect(container.contains(dialog)).toBe(false);
    expect(document.body.contains(dialog)).toBe(true);
  });

  it('closes on Escape', () => {
    const onClose = vi.fn();
    render(
      <Modal open onClose={onClose} ariaLabel="Example">
        <ModalBody>content</ModalBody>
      </Modal>,
    );
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('closes on backdrop mousedown but not on panel mousedown', () => {
    const onClose = vi.fn();
    render(
      <Modal open onClose={onClose} ariaLabel="Example">
        <ModalBody>content</ModalBody>
      </Modal>,
    );

    const dialog = screen.getByRole('dialog', { name: 'Example' });
    const scrim = dialog.parentElement as HTMLElement;

    fireEvent.mouseDown(dialog);
    fireEvent.mouseDown(screen.getByText('content'));
    expect(onClose).not.toHaveBeenCalled();

    fireEvent.mouseDown(scrim);
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('does not steal focus when the parent re-renders with a fresh onClose arrow', () => {
    const { rerender } = render(
      <Modal open onClose={() => {}} ariaLabel="Example">
        <ModalBody>
          <input aria-label="Name" />
        </ModalBody>
      </Modal>,
    );

    const input = screen.getByLabelText('Name');
    input.focus();
    expect(document.activeElement).toBe(input);

    // New inline arrow every render — must not tear down the focus effect.
    rerender(
      <Modal open onClose={() => {}} ariaLabel="Example">
        <ModalBody>
          <input aria-label="Name" />
        </ModalBody>
      </Modal>,
    );

    expect(document.activeElement).toBe(input);
  });

  it('renders ModalHeader close button as type=button', () => {
    render(
      <Modal open onClose={() => {}} ariaLabel="Example">
        <ModalHeader emphasis="title" onClose={() => {}} />
      </Modal>,
    );
    expect(screen.getByRole('button', { name: 'Close' })).toHaveAttribute('type', 'button');
  });
});
