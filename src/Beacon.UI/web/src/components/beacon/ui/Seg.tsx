import * as React from 'react';
import { cn } from '@/lib/cn';

export interface SegOption<T extends string = string> {
  value: T;
  label: React.ReactNode;
}

export function Seg<T extends string>({
  options,
  value,
  onChange,
  className,
  ariaLabel = 'View options',
}: {
  options: SegOption<T>[];
  value: T;
  onChange: (v: T) => void;
  className?: string;
  /** Accessible name for the radio group, announced by screen readers. */
  ariaLabel?: string;
}) {
  const buttonRefs = React.useRef<Array<HTMLButtonElement | null>>([]);

  const selectedIndex = options.findIndex(o => o.value === value);
  const tabbableIndex = selectedIndex === -1 ? 0 : selectedIndex;

  const selectAndFocus = (index: number) => {
    onChange(options[index].value);
    buttonRefs.current[index]?.focus();
  };

  const onKeyDown = (e: React.KeyboardEvent, index: number) => {
    if (e.key === 'ArrowRight' || e.key === 'ArrowDown') {
      e.preventDefault();
      selectAndFocus((index + 1) % options.length);
    } else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') {
      e.preventDefault();
      selectAndFocus((index - 1 + options.length) % options.length);
    } else if (e.key === 'Home') {
      e.preventDefault();
      selectAndFocus(0);
    } else if (e.key === 'End') {
      e.preventDefault();
      selectAndFocus(options.length - 1);
    }
  };

  return (
    <div
      role="radiogroup"
      aria-label={ariaLabel}
      className={cn(
        'inline-flex items-center bg-surface-2 border border-border rounded-sm p-0.5 text-xs',
        className,
      )}
    >
      {options.map((o, i) => (
        <button
          key={o.value}
          ref={el => {
            buttonRefs.current[i] = el;
          }}
          type="button"
          role="radio"
          aria-checked={o.value === value}
          tabIndex={i === tabbableIndex ? 0 : -1}
          onClick={() => onChange(o.value)}
          onKeyDown={e => onKeyDown(e, i)}
          className={cn(
            'px-2.5 py-1 rounded-xs font-medium transition',
            o.value === value
              ? 'bg-surface text-text shadow-sm'
              : 'text-text-muted hover:text-text',
          )}
        >
          {o.label}
        </button>
      ))}
    </div>
  );
}
