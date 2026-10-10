import { Link } from 'react-router-dom';
import { Pill } from '@/components/beacon';
import type { DataSourceProjectChip } from './queries';

const CHIP = 'inline-flex items-center rounded-xs border border-border-strong bg-surface-2 px-1.5 py-0.5 text-xs text-text';

interface ProjectChipsProps {
  /** The linking projects the caller may read. */
  projects: DataSourceProjectChip[];
  /** Linking projects the caller may not read: shown as a count only, never by name. */
  otherProjectCount: number;
  /** Whether the source is reachable while no project links it. */
  openWhenUnlinked: boolean;
  /** Chips link to the project pages (detail views); plain chips inside clickable rows. */
  linked?: boolean;
}

/** The projects a data source belongs to: the readable ones by name, plus "+N other" for the rest. */
export function ProjectChips({ projects, otherProjectCount, openWhenUnlinked, linked = false }: ProjectChipsProps) {
  if (projects.length === 0 && otherProjectCount === 0) {
    return openWhenUnlinked
      ? <span className="text-xs text-text-muted">Not linked</span>
      : <Pill tone="warn" title="Linked to no project: only Admins can use it until it is linked">Admins only</Pill>;
  }

  return (
    <span className="flex flex-wrap items-center gap-1">
      {projects.map(x =>
        linked
          ? <Link key={x.id} to={`/projects/${x.id}`} className={`${CHIP} hover:border-brand-500`}>{x.name}</Link>
          : <span key={x.id} className={CHIP}>{x.name}</span>,
      )}
      {otherProjectCount > 0 && (
        <span className="text-xs text-text-muted" title="Projects you do not have access to">
          +{otherProjectCount} other
        </span>
      )}
    </span>
  );
}
