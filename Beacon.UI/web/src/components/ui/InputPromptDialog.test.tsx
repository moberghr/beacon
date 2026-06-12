import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { InputPromptDialog } from './InputPromptDialog';

describe('InputPromptDialog', () => {
  it('confirms on Enter when not busy', () => {
    const onConfirm = vi.fn();
    render(
      <InputPromptDialog
        open
        title="Rename"
        label="Name"
        initialValue="alpha"
        onConfirm={onConfirm}
        onCancel={() => {}}
      />,
    );

    fireEvent.keyDown(screen.getByLabelText('Name'), { key: 'Enter' });
    expect(onConfirm).toHaveBeenCalledTimes(1);
    expect(onConfirm).toHaveBeenCalledWith('alpha');
  });

  it('does not double-fire on Enter while busy', () => {
    const onConfirm = vi.fn();
    render(
      <InputPromptDialog
        open
        title="Rename"
        label="Name"
        initialValue="alpha"
        busy
        onConfirm={onConfirm}
        onCancel={() => {}}
      />,
    );

    fireEvent.keyDown(screen.getByLabelText('Name'), { key: 'Enter' });
    fireEvent.keyDown(screen.getByLabelText('Name'), { key: 'Enter' });
    expect(onConfirm).not.toHaveBeenCalled();
  });
});
