import { Pill } from '@/components/beacon';

/** What a data source without database-level read-only enforcement needs from whoever connects it. */
export const READ_ONLY_LOGIN_HINT =
  "This engine does not refuse writes in Beacon's read-only session: Beacon's SQL checks are the only guard, so connect the data source with a login that can only read.";

/** What the engine's own read-only session adds where it refuses writes (PostgreSQL, MySQL), and what it does not. */
export const ENGINE_READ_ONLY_HINT =
  "This engine refuses writes in Beacon's read-only session. That is defence in depth, not a replacement: still connect the data source with a login that can only read.";

/** What a migration write target is, for whoever looks at one. */
export const WRITE_TARGET_HINT =
  'An Admin marked this data source as a migration write target: migrations write to it and Beacon never reads from it (no queries, previews, subscriptions, MCP or AI), so its login may hold the write grants a migration needs. Every other data source should use a login that can only read.';

/**
 * A warning on a data source whose engine does not refuse writes in Beacon's read-only session. Nothing is shown when the
 * engine does (PostgreSQL, MySQL) or when the source runs no SQL. A migration write target is never read, so it is
 * labelled as one instead.
 */
export function ReadOnlyEnforcementBadge({
  enforced,
  writeTarget,
}: {
  enforced?: boolean | null;
  writeTarget?: boolean;
}) {
  if (writeTarget) {
    return (
      <Pill tone="info" title={WRITE_TARGET_HINT}>
        Migration write target
      </Pill>
    );
  }
  if (enforced !== false) return null;
  return (
    <Pill tone="warn" title={READ_ONLY_LOGIN_HINT}>
      Read-only login required
    </Pill>
  );
}
