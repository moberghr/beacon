import type { ReactNode } from 'react';
import { cn } from '@/lib/cn';

interface EmptyStateProps {
  icon?: ReactNode;
  title: string;
  description?: string;
  action?: ReactNode;
  className?: string;
}

/**
 * Centered empty-state for tables, lists, and panels.
 */
export function EmptyState({ icon, title, description, action, className }: EmptyStateProps) {
  return (
    <div
      className={cn(
        'flex items-start gap-3 p-5 m-4 rounded-md border border-dashed border-border bg-surface-2',
        className,
      )}
    >
      {icon && (
        <div className="shrink-0 size-9 grid place-items-center rounded-sm bg-surface text-text-muted [&>svg]:size-4">
          {icon}
        </div>
      )}
      <div className="flex-1 min-w-0">
        <div className="text-sm font-medium text-text">{title}</div>
        {description && <div className="text-xs text-text-muted mt-0.5">{description}</div>}
      </div>
      {action && <div className="shrink-0">{action}</div>}
    </div>
  );
}
