import type { ReactNode } from 'react';
import { cn } from '@/lib/cn';

export interface TabDef<K extends string> {
  key: K;
  label: ReactNode;
  count?: number;
}

interface TabsProps<K extends string> {
  tabs: TabDef<K>[];
  active: K;
  onChange: (key: K) => void;
  trailing?: ReactNode;
}

/**
 * Horizontal tabs with an optional count badge per tab and a trailing slot
 * (typically for action buttons aligned to the right of the tab strip).
 */
export function Tabs<K extends string>({ tabs, active, onChange, trailing }: TabsProps<K>) {
  return (
    <div className="flex items-stretch border-b border-border">
      {tabs.map(t => {
        const isActive = t.key === active;
        return (
          <button
            key={t.key}
            type="button"
            onClick={() => onChange(t.key)}
            className={cn(
              'inline-flex items-center gap-1.5 px-3 py-2 text-sm font-medium transition border-b-2 -mb-px',
              isActive
                ? 'text-text border-brand-500'
                : 'text-text-muted border-transparent hover:text-text',
            )}
          >
            {t.label}
            {typeof t.count === 'number' && (
              <span
                className={cn(
                  'inline-flex items-center justify-center min-w-[20px] h-[16px] px-1 rounded-xs text-[10px] mono',
                  isActive
                    ? 'bg-brand-100 text-brand-700'
                    : 'bg-surface-2 text-text-muted border border-border',
                )}
              >
                {t.count}
              </span>
            )}
          </button>
        );
      })}
      {trailing && <div className="ml-auto flex items-center gap-1.5 pr-2 py-1">{trailing}</div>}
    </div>
  );
}
