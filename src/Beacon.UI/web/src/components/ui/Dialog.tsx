import { useEffect, useRef, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { X } from 'lucide-react';
import { cn } from '@/lib/cn';

export type DialogSize = 'sm' | 'md' | 'lg' | 'xl';

interface DialogProps {
  open: boolean;
  onClose: () => void;
  title: ReactNode;
  sub?: ReactNode;
  size?: DialogSize;
  children: ReactNode;
  footer?: ReactNode;
  closeOnBackdrop?: boolean;
  closeOnEscape?: boolean;
  ariaLabel?: string;
}

const sizeWidth: Record<DialogSize, number> = {
  sm: 420,
  md: 580,
  lg: 760,
  xl: 960,
};

/**
 * Reusable dialog shell. Esc key closes (configurable); click on backdrop closes (configurable).
 * Focus moves to the dialog on open and is restored on close. Body scroll is
 * locked while open.
 */
export function Dialog({
  open,
  onClose,
  title,
  sub,
  size = 'md',
  children,
  footer,
  closeOnBackdrop = true,
  closeOnEscape = true,
  ariaLabel,
}: DialogProps) {
  const dialogRef = useRef<HTMLDivElement>(null);
  const previouslyFocused = useRef<Element | null>(null);

  // Latest onClose without invalidating the effect — callers passing fresh
  // arrows shouldn't steal focus from inputs on every keystroke.
  const onCloseRef = useRef(onClose);
  const closeOnEscapeRef = useRef(closeOnEscape);
  useEffect(() => {
    onCloseRef.current = onClose;
    closeOnEscapeRef.current = closeOnEscape;
  });

  useEffect(() => {
    if (!open) {
      return;
    }

    previouslyFocused.current = document.activeElement;
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';

    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.stopPropagation();
        if (closeOnEscapeRef.current) {
          onCloseRef.current();
        }
        return;
      }
      if (e.key === 'Tab') {
        const container = dialogRef.current;
        if (!container) {
          return;
        }
        const focusable = container.querySelectorAll<HTMLElement>(
          'a[href], button:not([disabled]), textarea:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])',
        );
        if (focusable.length === 0) {
          e.preventDefault();
          container.focus();
          return;
        }
        const first = focusable[0];
        const last = focusable[focusable.length - 1];
        const active = document.activeElement;
        if (e.shiftKey && (active === first || active === container)) {
          e.preventDefault();
          last.focus();
        } else if (!e.shiftKey && active === last) {
          e.preventDefault();
          first.focus();
        }
      }
    };
    document.addEventListener('keydown', onKey);

    requestAnimationFrame(() => {
      dialogRef.current?.focus();
    });

    return () => {
      document.removeEventListener('keydown', onKey);
      document.body.style.overflow = previousOverflow;
      const prev = previouslyFocused.current;
      if (prev instanceof HTMLElement) {
        prev.focus();
      }
    };
  }, [open]);

  if (!open) {
    return null;
  }

  const onScrimClick = (e: React.MouseEvent<HTMLDivElement>) => {
    if (e.target === e.currentTarget && closeOnBackdrop) {
      onClose();
    }
  };

  return createPortal(
    <div
      onMouseDown={onScrimClick}
      className="fixed inset-0 z-50 grid place-items-start justify-center pt-[8vh] px-4 bg-black/35 backdrop-blur-sm"
    >
      <div
        ref={dialogRef}
        role="dialog"
        aria-modal="true"
        aria-label={ariaLabel ?? (typeof title === 'string' ? title : undefined)}
        tabIndex={-1}
        style={{ width: '100%', maxWidth: sizeWidth[size] }}
        className={cn(
          'bg-surface border border-border rounded-lg shadow-pop overflow-hidden flex flex-col max-h-[84vh] outline-none',
        )}
      >
        <div className="flex items-start gap-3 px-5 pt-4 pb-3 border-b border-border bg-gradient-to-b from-surface to-surface-2">
          <div className="flex-1 min-w-0">
            <h2 className="m-0 text-base font-semibold text-text tracking-tighter">{title}</h2>
            {sub && <p className="m-0 mt-1 text-sm text-text-muted">{sub}</p>}
          </div>
          <button
            type="button"
            onClick={onClose}
            aria-label="Close dialog"
            className="shrink-0 -mr-1 -mt-1 size-8 grid place-items-center rounded-sm text-text-muted hover:bg-surface-2 hover:text-text transition"
          >
            <X size={16} />
          </button>
        </div>
        <div className="p-5 overflow-y-auto flex-1">{children}</div>
        {footer && (
          <div className="flex items-center justify-end gap-1.5 px-5 py-3 border-t border-border bg-surface-2">
            {footer}
          </div>
        )}
      </div>
    </div>,
    document.body,
  );
}
