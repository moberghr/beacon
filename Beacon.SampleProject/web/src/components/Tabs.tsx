import type { ReactNode } from 'react';

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
 * Tabs styled per design-system `.tabs` / `.tab` (see styles-beacon.css §920+).
 * Generic so the active key is type-safe.
 */
export function Tabs<K extends string>({ tabs, active, onChange, trailing }: TabsProps<K>) {
  return (
    <div className="tabs">
      {tabs.map(t => (
        <button
          key={t.key}
          type="button"
          className={'tab' + (t.key === active ? ' active' : '')}
          onClick={() => onChange(t.key)}
        >
          {t.label}
          {typeof t.count === 'number' && <span className="tab__count">{t.count}</span>}
        </button>
      ))}
      {trailing && (
        <div style={{ marginLeft: 'auto', padding: '6px 16px 6px 0', display: 'flex', gap: 6 }}>
          {trailing}
        </div>
      )}
    </div>
  );
}
