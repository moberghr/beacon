import { useState } from 'react';
import { toast } from 'sonner';
import { Button, Pill } from '@/components/beacon';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { useSetDataSourceEnabled, type DataSourceEntry } from './queries';
import { DISABLED_HINT } from './DataSourceStateBadges';

/**
 * Whether the data source is enabled, and for a DataSourceAdmin the switch (never for a host-managed source, which the
 * page does not offer it for). Disabling asks for confirmation, since every run, query and MCP call on the source fails
 * from then on; enabling is immediate.
 */
export function DataSourceEnabledControl({ entry, canManage }: { entry: DataSourceEntry; canManage: boolean }) {
  const mutation = useSetDataSourceEnabled();
  const [confirming, setConfirming] = useState(false);
  const disabled = entry.disabled === true;

  const change = async (enabled: boolean) => {
    try {
      await mutation.mutateAsync({ id: entry.id, enabled });
      toast.success(enabled ? `Enabled data source '${entry.name}'.` : `Disabled data source '${entry.name}'.`);
      setConfirming(false);
    } catch {
      // Error toast already raised by the mutation hook.
    }
  };

  return (
    <span className="inline-flex flex-wrap items-center gap-2">
      {disabled
        ? <Pill tone="crit" title={DISABLED_HINT}>Disabled</Pill>
        : <Pill tone="ok">Enabled</Pill>}
      {canManage && (
        <Button
          size="sm"
          variant={disabled ? undefined : 'danger'}
          disabled={mutation.isPending}
          onClick={() => (disabled ? change(true) : setConfirming(true))}
        >
          {disabled ? 'Enable' : 'Disable'}
        </Button>
      )}
      <ConfirmDialog
        open={confirming}
        title="Disable data source"
        message={
          <>
            Beacon will make no connection with <strong>{entry.name}</strong>: every query, preview, subscription run,
            data-quality check, MCP call and AI path that reads it fails until it is enabled again. Nothing is deleted. The
            change is recorded in the security audit.
          </>
        }
        confirmLabel="Disable"
        destructive
        busy={mutation.isPending}
        onConfirm={() => change(false)}
        onCancel={() => setConfirming(false)}
      />
    </span>
  );
}
