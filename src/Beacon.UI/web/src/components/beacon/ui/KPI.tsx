import * as React from 'react';
import { cn } from '@/lib/cn';

type Dot = 'brand' | 'ok' | 'warn' | 'crit' | 'info';

const dotClass: Record<Dot, string> = {
  brand: 'bg-brand-500',
  ok: 'bg-ok',
  warn: 'bg-warn',
  crit: 'bg-crit',
  info: 'bg-info',
};

export interface KPIProps extends React.HTMLAttributes<HTMLDivElement> {
  dot?: Dot;
  label: React.ReactNode;
  value: React.ReactNode;
  unit?: React.ReactNode;
  sub?: React.ReactNode;
}

export function KPI({
  dot = 'brand',
  label,
  value,
  unit,
  sub,
  className,
  ...props
}: KPIProps) {
  return (
    <div
      className={cn(
        'bg-surface border border-border rounded-md p-4 flex flex-col gap-1.5 min-w-0',
        className,
      )}
      {...props}
    >
      <div className="flex items-center gap-2">
        <span className={cn('inline-block w-1.5 h-1.5 rounded-full', dotClass[dot])} />
        <span className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">
          {label}
        </span>
      </div>
      <div className="flex items-baseline gap-1 mt-0.5">
        <span className="text-[30px] font-semibold leading-none tracking-tighter">{value}</span>
        {unit && <span className="text-sm text-text-muted">{unit}</span>}
      </div>
      {sub && <div className="text-xs text-text-muted mt-0.5">{sub}</div>}
    </div>
  );
}

export function KPIGrid({ className, ...props }: React.HTMLAttributes<HTMLDivElement>) {
  return (
    <div
      className={cn('grid gap-4 grid-cols-[repeat(auto-fit,minmax(180px,1fr))]', className)}
      {...props}
    />
  );
}
