import { useEffect, useRef, useState, type ReactNode } from 'react';
import { Dialog } from './Dialog';
import { Button, Field, Input } from '@/components/beacon';

interface InputPromptDialogProps {
  open: boolean;
  title: string;
  message?: ReactNode;
  label: string;
  placeholder?: string;
  initialValue?: string;
  confirmLabel?: string;
  cancelLabel?: string;
  busy?: boolean;
  /** Optional validator. Return an error string to block submission, null for OK. */
  validate?: (raw: string) => string | null;
  onConfirm: (value: string) => void;
  onCancel: () => void;
}

/**
 * Modal replacement for `window.prompt(...)`. Keeps focus management,
 * keyboard handling (Esc/Enter), and styling consistent with the rest
 * of the Beacon dialog surface. The host page is responsible for the
 * open/close lifecycle and for acting on the confirmed value.
 */
export function InputPromptDialog({
  open,
  title,
  message,
  label,
  placeholder,
  initialValue = '',
  confirmLabel = 'OK',
  cancelLabel = 'Cancel',
  busy = false,
  validate,
  onConfirm,
  onCancel,
}: InputPromptDialogProps) {
  const inputRef = useRef<HTMLInputElement>(null);
  const [value, setValue] = useState(initialValue);
  const [error, setError] = useState<string | null>(null);

  // Re-seed the field every time the dialog opens so reuse across calls is clean.
  useEffect(() => {
    if (open) {
      setValue(initialValue);
      setError(null);
      // Focus the input after the dialog mounts.
      const t = window.setTimeout(() => inputRef.current?.focus(), 0);
      return () => window.clearTimeout(t);
    }
  }, [open, initialValue]);

  const handleSubmit = () => {
    if (busy) return;
    const validationError = validate?.(value) ?? null;
    if (validationError !== null) {
      setError(validationError);
      return;
    }
    onConfirm(value);
  };

  return (
    <Dialog
      open={open}
      onClose={onCancel}
      title={title}
      size="sm"
      footer={
        <>
          <Button onClick={onCancel} disabled={busy}>
            {cancelLabel}
          </Button>
          <Button variant="primary" onClick={handleSubmit} disabled={busy}>
            {busy ? 'Working…' : confirmLabel}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-3 text-sm text-text">
        {message ? <div className="leading-relaxed">{message}</div> : null}
        <Field
          label={label}
          hint={error ? <span className="text-crit">{error}</span> : undefined}
        >
          <Input
            ref={inputRef}
            value={value}
            placeholder={placeholder}
            onChange={(e) => {
              setValue(e.target.value);
              if (error) setError(null);
            }}
            onKeyDown={(e) => {
              if (e.key === 'Enter') {
                e.preventDefault();
                handleSubmit();
              }
            }}
          />
        </Field>
      </div>
    </Dialog>
  );
}
