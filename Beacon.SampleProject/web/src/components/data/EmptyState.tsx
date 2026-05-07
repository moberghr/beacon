import type { ReactNode } from 'react';

interface EmptyStateProps {
  icon?: ReactNode;
  title: string;
  description?: string;
  action?: ReactNode;
  className?: string;
}

/**
 * Matches `.empty-state` in styles-beacon.css. Drop into any container.
 */
export function EmptyState({ icon, title, description, action, className }: EmptyStateProps) {
  return (
    <div className={`empty-state${className ? ` ${className}` : ''}`}>
      {icon && <div className="empty-state__icon">{icon}</div>}
      <div style={{ flex: 1 }}>
        <div className="empty-state__title">{title}</div>
        {description && <div className="empty-state__sub">{description}</div>}
      </div>
      {action}
    </div>
  );
}
