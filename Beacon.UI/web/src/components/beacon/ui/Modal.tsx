import * as React from 'react';
import { cn } from '@/lib/cn';
import { Kbd } from './Kbd';

/**
 * Beacon dialog. Pair <Modal> with <ModalHeader> + <ModalBody> + <ModalFooter>.
 * Esc closes.
 */
export function Modal({
  open,
  onClose,
  children,
  width = 580,
  className,
}: {
  open: boolean;
  onClose: () => void;
  children: React.ReactNode;
  width?: number;
  className?: string;
}) {
  React.useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, onClose]);

  if (!open) return null;

  return (
    <div
      onClick={onClose}
      className="fixed inset-0 z-50 grid place-items-start justify-center pt-[8vh] px-4 bg-black/35 backdrop-blur-sm"
    >
      <div
        onClick={e => e.stopPropagation()}
        style={{ width: '100%', maxWidth: width }}
        className={cn(
          'bg-surface border border-border rounded-lg shadow-pop overflow-hidden flex flex-col max-h-[84vh]',
          className,
        )}
      >
        {children}
      </div>
    </div>
  );
}

export function ModalHeader({
  eyebrow,
  prefix,
  emphasis,
  suffix,
  sub,
  variant = 'signal',
  onClose,
}: {
  eyebrow?: React.ReactNode;
  prefix?: React.ReactNode;
  emphasis: React.ReactNode;
  suffix?: React.ReactNode;
  sub?: React.ReactNode;
  variant?: 'signal' | 'nodes';
  onClose?: () => void;
}) {
  return (
    <div className="relative isolate overflow-hidden border-b border-border bg-gradient-to-b from-surface to-surface-2">
      <svg
        className="absolute inset-0 w-full h-full text-text-subtle opacity-[0.12] pointer-events-none"
        aria-hidden
      >
        <defs>
          <pattern id="ph-grid" width="24" height="24" patternUnits="userSpaceOnUse">
            <path d="M 24 0 L 0 0 0 24" fill="none" stroke="currentColor" strokeWidth="0.5" />
          </pattern>
        </defs>
        <rect width="100%" height="100%" fill="url(#ph-grid)" />
      </svg>

      {variant === 'signal' && <span className="beacon-beam" aria-hidden />}

      <div className="relative px-5 pt-4 pb-4 flex items-start gap-3">
        <div className="flex-1 min-w-0">
          {eyebrow && (
            <div className="eyebrow mb-2">
              <span className="eyebrow-pin" />
              {eyebrow}
            </div>
          )}
          <h2 className="m-0 text-[22px] font-semibold leading-tight tracking-tighter">
            {prefix && <span className="text-text">{prefix} </span>}
            <span className="relative inline-block italic font-semibold text-brand-700 dark:text-brand-300">
              {emphasis}
              <svg
                className="beacon-underline"
                viewBox="0 0 240 12"
                preserveAspectRatio="none"
                aria-hidden
              >
                <path
                  d="M2 8 C 40 2, 80 11, 120 6 S 200 3, 238 7"
                  fill="none"
                  stroke="var(--brand-500)"
                  strokeWidth="2"
                  strokeLinecap="round"
                />
              </svg>
            </span>
            {suffix && <span className="text-text"> {suffix}</span>}
            {prefix === undefined && suffix === undefined ? '.' : ''}
          </h2>
          {sub && <p className="m-0 mt-2 text-sm text-text-muted">{sub}</p>}
        </div>
        {onClose && (
          <button
            onClick={onClose}
            className="shrink-0 -mr-1 -mt-1 size-8 grid place-items-center rounded-sm text-text-muted hover:bg-surface-2 hover:text-text"
            aria-label="Close"
          >
            ✕
          </button>
        )}
      </div>
    </div>
  );
}

export function ModalBody({ className, ...props }: React.HTMLAttributes<HTMLDivElement>) {
  return <div className={cn('p-5 overflow-y-auto flex-1', className)} {...props} />;
}

export function ModalFooter({
  className,
  hints,
  children,
}: {
  className?: string;
  hints?: React.ReactNode;
  children: React.ReactNode;
}) {
  return (
    <div
      className={cn(
        'flex items-center gap-2 px-5 py-3 border-t border-border bg-surface-2',
        className,
      )}
    >
      <div className="text-2xs text-text-muted flex items-center gap-1.5">
        {hints ?? (
          <>
            <Kbd>Esc</Kbd> close · <Kbd>⌘</Kbd>
            <Kbd>↵</Kbd> submit
          </>
        )}
      </div>
      <div className="ml-auto flex items-center gap-1.5">{children}</div>
    </div>
  );
}
