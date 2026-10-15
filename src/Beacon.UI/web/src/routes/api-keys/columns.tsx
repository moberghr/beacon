import { Pill } from '@/components/beacon';
import { formatDateTime } from '@/lib/format';

function scopeTone(scope: string): 'warn' | 'info' | 'neutral' {
  switch (scope) {
    case 'Execute': return 'warn';
    case 'Read': return 'info';
    default: return 'neutral';
  }
}

export function ScopePills({ scopes }: { scopes: string[] }) {
  return (
    <span className="inline-flex gap-1 flex-wrap">
      {scopes.map(s => <Pill key={s} tone={scopeTone(s)}>{s}</Pill>)}
    </span>
  );
}

/** The projects a key is restricted to; a dash when it is not restricted. */
export function ProjectsCell({ ids }: { ids: number[] | null }) {
  if (ids == null) return <span className="text-text-muted">—</span>;
  if (ids.length === 0) return <span className="text-text-muted">None</span>;
  return <span className="mono text-xs">{ids.map(x => `#${x}`).join(', ')}</span>;
}

export function ExpiresCell({ expiresAt }: { expiresAt: string | null }) {
  if (!expiresAt) return <span className="text-text-muted">Never</span>;
  const date = new Date(expiresAt);
  const expired = !Number.isNaN(date.getTime()) && date < new Date();
  return expired
    ? <Pill tone="crit">Expired</Pill>
    : <Pill>{formatDateTime(expiresAt)}</Pill>;
}

/**
 * Whether the key works now. Inactive covers a revoked or expired key and one whose owner is disabled or archived;
 * `revokedAt` (administrators' list) names a revocation.
 */
export function StatusPill({ isActive, revokedAt }: { isActive: boolean; revokedAt?: string | null }) {
  if (isActive) return <Pill tone="ok">Active</Pill>;
  return revokedAt
    ? <Pill tone="crit">Revoked</Pill>
    : <Pill tone="crit">Inactive</Pill>;
}
