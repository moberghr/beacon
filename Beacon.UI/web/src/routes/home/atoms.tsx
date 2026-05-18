import { type ReactNode } from 'react';
import {
  ArrowDown,
  ArrowUp,
  ChevronRight,
  Info,
  type LucideIcon,
} from 'lucide-react';
import { cn } from '@/lib/cn';

export type DotTone = 'brand' | 'ok' | 'warn' | 'crit' | 'info';

const dotClass: Record<DotTone, string> = {
  brand: 'bg-brand-500',
  ok: 'bg-ok',
  warn: 'bg-warn',
  crit: 'bg-crit',
  info: 'bg-info',
};

export interface HomeKpiProps {
  dot?: DotTone;
  label: string;
  value: ReactNode;
  unit?: string;
  sub?: ReactNode;
  delta?: string;
  deltaDir?: 'up' | 'down';
  spark?: ReactNode;
}

export function HomeKpi({
  dot = 'brand',
  label,
  value,
  unit,
  sub,
  delta,
  deltaDir,
  spark,
}: HomeKpiProps) {
  const deltaIsImprovement = deltaDir === 'down';
  return (
    <div className="bg-surface border border-border rounded-md p-4 flex flex-col gap-1.5 min-w-0">
      <div className="flex items-center gap-2">
        <span className={cn('inline-block w-1.5 h-1.5 rounded-full', dotClass[dot])} />
        <span className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">{label}</span>
      </div>
      <div className="flex items-baseline gap-1 mt-0.5">
        <span className="text-[26px] font-semibold leading-none tracking-tighter">{value}</span>
        {unit && <span className="text-sm text-text-muted">{unit}</span>}
      </div>
      <div className="flex items-center gap-2 text-xs text-text-muted mt-0.5">
        {delta && (
          <span
            className={cn(
              'inline-flex items-center gap-0.5 mono',
              deltaIsImprovement ? 'text-ok' : 'text-text-muted',
            )}
          >
            {deltaDir === 'up' ? <ArrowUp size={11} /> : <ArrowDown size={11} />}
            {delta}
          </span>
        )}
        {sub && <span>{sub}</span>}
      </div>
      {spark && <div className="-mb-1 -mx-1 mt-1">{spark}</div>}
    </div>
  );
}

export interface StatRowProps {
  Icon: LucideIcon;
  label: string;
  value: string;
  trail?: ReactNode;
}

export function StatRow({ Icon, label, value, trail }: StatRowProps) {
  return (
    <div className="flex items-center gap-3 px-4 py-2.5 border-b border-border last:border-b-0 hover:bg-surface-2 transition">
      <div className="size-7 rounded-sm bg-surface-2 grid place-items-center text-text-muted">
        <Icon size={15} />
      </div>
      <div className="flex-1 min-w-0">
        <div className="text-xs text-text-muted">{label}</div>
        <div className="text-sm font-medium mono">{value}</div>
      </div>
      {trail}
      <ChevronRight size={14} className="text-text-subtle" />
    </div>
  );
}

export interface MiniProps {
  color: string;
  label: string;
  value: string;
  bar: string;
}

export function Mini({ color, label, value, bar }: MiniProps) {
  return (
    <div className="flex flex-col gap-1.5 p-4 border-r border-border last:border-r-0">
      <div className="flex items-center gap-1.5 text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">
        <span className="inline-block w-1.5 h-1.5 rounded-full" style={{ background: color }} />
        {label}
      </div>
      <div className="text-xl font-semibold mono tracking-tighter">{value}</div>
      <div className="h-1 rounded-full bg-surface-2 overflow-hidden">
        <span className="block h-full rounded-full" style={{ width: bar, background: color }} />
      </div>
    </div>
  );
}

const feedTone: Record<string, { bg: string; fg: string }> = {
  ok: { bg: 'bg-ok-bg', fg: 'text-ok' },
  warn: { bg: 'bg-warn-bg', fg: 'text-warn' },
  crit: { bg: 'bg-crit-bg', fg: 'text-crit' },
  info: { bg: 'bg-info-bg', fg: 'text-info' },
  brand: { bg: 'bg-brand-50', fg: 'text-brand-600' },
};

export interface FeedItemProps {
  tone?: string;
  Icon?: LucideIcon;
  title: string;
  meta?: string | null;
  time: string;
}

export function FeedItem({ tone = 'info', Icon = Info, title, meta, time }: FeedItemProps) {
  const c = feedTone[tone] ?? feedTone.info;
  return (
    <div className="flex items-start gap-2.5 px-4 py-2.5 border-b border-border last:border-b-0">
      <div className={cn('shrink-0 size-6 grid place-items-center rounded-sm', c.bg, c.fg)}>
        <Icon size={12} />
      </div>
      <div className="flex-1 min-w-0">
        <div className="text-sm text-text">{title}</div>
        {meta && <div className="text-xs text-text-muted mt-0.5">{meta}</div>}
      </div>
      <div className="text-2xs text-text-subtle mono whitespace-nowrap pt-0.5">{time}</div>
    </div>
  );
}
