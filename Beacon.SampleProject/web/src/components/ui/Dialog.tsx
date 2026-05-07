import { useEffect, useRef, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { Icon } from '@/components/Icon';

export type DialogSize = 'sm' | 'md' | 'lg';

interface DialogProps {
  open: boolean;
  onClose: () => void;
  title: ReactNode;
  sub?: ReactNode;
  size?: DialogSize;
  children: ReactNode;
  footer?: ReactNode;
  closeOnBackdrop?: boolean;
  ariaLabel?: string;
}

/**
 * Reusable dialog shell using Beacon-design `.modal-scrim` / `.modal` styles.
 * Esc key closes; click on backdrop closes (configurable). Focus is moved to
 * the dialog on open and restored on close. Body scroll is locked while open.
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
  ariaLabel,
}: DialogProps) {
  const dialogRef = useRef<HTMLDivElement>(null);
  const previouslyFocused = useRef<Element | null>(null);

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
        onClose();
      }
    };
    document.addEventListener('keydown', onKey);

    // Focus the dialog after paint so screen readers announce it.
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
  }, [open, onClose]);

  if (!open) {
    return null;
  }

  const onScrimClick = (e: React.MouseEvent<HTMLDivElement>) => {
    if (e.target === e.currentTarget && closeOnBackdrop) {
      onClose();
    }
  };

  return createPortal(
    <div className="modal-scrim" onMouseDown={onScrimClick}>
      <div
        ref={dialogRef}
        className={`modal modal--${size}`}
        role="dialog"
        aria-modal="true"
        aria-label={ariaLabel ?? (typeof title === 'string' ? title : undefined)}
        tabIndex={-1}
      >
        <div className="modal__head">
          <div className="modal__head-inner">
            <div className="modal__head-main">
              <h2 className="modal__title">{title}</h2>
              {sub && <p className="modal__sub">{sub}</p>}
            </div>
            <button
              type="button"
              className="btn btn--ghost modal__close"
              onClick={onClose}
              aria-label="Close dialog"
            >
              <Icon.X size={16} />
            </button>
          </div>
        </div>
        <div className="modal__body">{children}</div>
        {footer && <div className="modal__foot">{footer}</div>}
      </div>
    </div>,
    document.body,
  );
}
