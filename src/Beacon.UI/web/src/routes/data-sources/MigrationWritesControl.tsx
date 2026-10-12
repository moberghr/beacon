import { useState } from 'react';
import { toast } from 'sonner';
import { Button, Pill } from '@/components/beacon';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { useAccess } from '@/auth/useAuth';
import { describeError, httpErrorInfo } from '@/lib/api';
import { useSetMigrationWrites, type DataSourceEntry } from './queries';
import { WRITE_TARGET_HINT } from './ReadOnlyEnforcementBadge';

/** The engines a migration can write to, as the data source entry names them. */
const WRITABLE_ENGINES = ['PostgreSQL', 'MySQL', 'MSSQL'];

/**
 * Whether the data source is a migration write target, and for an Admin the switch. Marking one needs a deployment that
 * runs migrations and a PostgreSQL, MySQL or SQL Server database; unmarking is always offered, so a target can go back to
 * being an ordinary read source. Both directions ask for confirmation: a target's login may hold write grants, which
 * every read would use once Beacon reads the source again. A source that queries, subscriptions or data contracts read
 * is marked only after a second confirmation that names what will stop working (the server refuses it otherwise).
 * Everyone else sees the state only.
 */
export function MigrationWritesControl({ entry }: { entry: DataSourceEntry }) {
  const access = useAccess();
  const mutation = useSetMigrationWrites();
  const [confirming, setConfirming] = useState<'mark' | 'unmark' | null>(null);
  const [inUse, setInUse] = useState<string | null>(null);
  const isTarget = entry.allowMigrationWrites === true;
  const canMark =
    access.migrationsEnabled &&
    entry.dataSourceType === 'Database' &&
    WRITABLE_ENGINES.includes(entry.databaseEngineType ?? '');
  const showsSwitch = access.isAdmin && (isTarget || canMark);

  const change = async (allowMigrationWrites: boolean, force = false) => {
    try {
      await mutation.mutateAsync({ id: entry.id, allowMigrationWrites, force });
      toast.success(allowMigrationWrites ? 'Marked as a migration write target.' : 'No longer a migration write target.');
      setConfirming(null);
      setInUse(null);
    } catch (err) {
      // The server refuses to mark a source that is still read until the Admin confirms; its reason names what reads it.
      if (allowMigrationWrites && !force && httpErrorInfo(err)?.status === 409) {
        setConfirming(null);
        setInUse(describeError(err, 'Queries, subscriptions or data contracts read this data source.'));
        return;
      }

      toast.error(describeError(err, 'Changing the migration write target failed'));
    }
  };

  return (
    <span className="inline-flex flex-wrap items-center gap-2">
      {isTarget
        ? <Pill tone="info" title={WRITE_TARGET_HINT}>Migration write target</Pill>
        : <span className="text-text-muted">No</span>}
      {showsSwitch && (
        <Button
          size="sm"
          disabled={mutation.isPending}
          onClick={() => setConfirming(isTarget ? 'unmark' : 'mark')}
        >
          {isTarget ? 'Return to a read source' : 'Mark as migration write target'}
        </Button>
      )}
      <ConfirmDialog
        open={confirming === 'mark'}
        title="Mark as migration write target"
        message={
          <>
            Migrations will be able to write to <strong>{entry.name}</strong>, and Beacon will stop reading it: queries,
            previews, subscriptions, data quality, MCP and AI will refuse it. Its login may then hold the write grants a
            migration needs. The change is recorded in the settings history.
          </>
        }
        confirmLabel="Mark as write target"
        busy={mutation.isPending}
        onConfirm={() => change(true)}
        onCancel={() => setConfirming(null)}
      />
      <ConfirmDialog
        open={confirming === 'unmark'}
        title="Return to a read source"
        message={
          <>
            Beacon will read <strong>{entry.name}</strong> again: queries, previews, subscriptions, data quality, MCP and AI
            may use it, with its login. Change its login to one that can only read first, so no write grant a migration
            needed reaches those reads. The change is recorded in the settings history.
          </>
        }
        confirmLabel="Return to reads"
        busy={mutation.isPending}
        onConfirm={() => change(false)}
        onCancel={() => setConfirming(null)}
      />
      <ConfirmDialog
        open={inUse !== null}
        title="Data source in use"
        message={inUse}
        confirmLabel="Mark anyway"
        busy={mutation.isPending}
        onConfirm={() => change(true, true)}
        onCancel={() => setInUse(null)}
      />
    </span>
  );
}
