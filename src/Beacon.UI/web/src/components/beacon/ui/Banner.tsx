import * as React from 'react';
import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from '@/lib/cn';

const banner = cva('flex items-start gap-3 p-3.5 rounded-md border', {
  variants: {
    tone: {
      ok: 'bg-ok-bg text-text border-ok/30',
      warn: 'bg-warn-bg text-text border-warn/30',
      crit: 'bg-crit-bg text-text border-crit/40',
      info: 'bg-info-bg text-text border-info/30',
    },
  },
  defaultVariants: { tone: 'info' },
});

const iconTone: Record<NonNullable<VariantProps<typeof banner>['tone']>, string> = {
  ok: 'text-ok',
  warn: 'text-warn',
  crit: 'text-crit',
  info: 'text-info',
};

export interface BannerProps
  extends Omit<React.HTMLAttributes<HTMLDivElement>, 'title'>,
    VariantProps<typeof banner> {
  icon?: React.ReactNode;
  title: React.ReactNode;
  sub?: React.ReactNode;
  actions?: React.ReactNode;
}

export function Banner({
  tone = 'info',
  icon,
  title,
  sub,
  actions,
  className,
  ...props
}: BannerProps) {
  return (
    <div className={cn(banner({ tone }), className)} {...props}>
      {icon && (
        <span className={cn('shrink-0 mt-0.5 [&>svg]:size-4', iconTone[tone!])}>{icon}</span>
      )}
      <div className="flex-1 min-w-0">
        <div className="text-sm font-medium leading-tight">{title}</div>
        {sub && <div className="text-xs text-text-muted mt-1">{sub}</div>}
      </div>
      {actions && <div className="ml-auto flex items-center gap-2 shrink-0">{actions}</div>}
    </div>
  );
}
