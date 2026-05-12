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
}: {
  options: SegOption<T>[];
  value: T;
  onChange: (v: T) => void;
  className?: string;
}) {
  return (
    <div
      className={cn(
        'inline-flex items-center bg-surface-2 border border-border rounded-sm p-0.5 text-xs',
        className,
      )}
    >
      {options.map(o => (
        <button
          key={o.value}
          type="button"
          onClick={() => onChange(o.value)}
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
