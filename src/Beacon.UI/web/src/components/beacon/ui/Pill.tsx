import * as React from 'react';
import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from '@/lib/cn';

const pill = cva(
  'inline-flex items-center gap-1.5 text-2xs font-semibold uppercase tracking-eyebrow rounded-xs px-1.5 py-0.5 border',
  {
    variants: {
      tone: {
        neutral: 'bg-surface-2 text-text-muted border-border-strong',
        ok: 'bg-ok-bg text-ok border-transparent',
        warn: 'bg-warn-bg text-warn border-transparent',
        crit: 'bg-crit-bg text-crit border-transparent',
        info: 'bg-info-bg text-info border-transparent',
      },
    },
    defaultVariants: { tone: 'neutral' },
  },
);

export interface PillProps
  extends React.HTMLAttributes<HTMLSpanElement>,
    VariantProps<typeof pill> {
  dot?: boolean;
}

export function Pill({ tone, dot, className, children, ...props }: PillProps) {
  return (
    <span className={cn(pill({ tone }), className)} {...props}>
      {dot && (
        <span
          className="inline-block rounded-full"
          style={{ width: 6, height: 6, background: 'currentColor' }}
        />
      )}
      {children}
    </span>
  );
}
