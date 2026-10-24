import { useEffect, useState } from 'react';
import { toast } from 'sonner';
import { Dialog } from '@/components/ui/Dialog';
import { Button } from '@/components/beacon';
import { FolderPicker } from './FolderPicker';
import { useMoveQueryToFolder } from './queries';

interface MoveQueryDialogProps {
  open: boolean;
  onClose: () => void;
  query: { queryId: number; name: string; folderId: number | null; folderPath: string | null } | null;
}

/** Pick the folder a saved query lives in; clearing the choice takes it out of any folder. */
export function MoveQueryDialog({ open, onClose, query }: MoveQueryDialogProps) {
  const [folderId, setFolderId] = useState<number | null>(null);
  const [folderLabel, setFolderLabel] = useState<string | null>(null);
  const move = useMoveQueryToFolder();

  useEffect(() => {
    if (!open) return;
    setFolderId(query?.folderId ?? null);
    setFolderLabel(query?.folderPath ?? null);
  }, [open, query]);

  const onSubmit = async () => {
    if (query == null) return;
    try {
      await move.mutateAsync({ queryId: query.queryId, folderId });
      toast.success(folderId == null ? `Removed '${query.name}' from its folder` : `Moved '${query.name}'`);
      onClose();
    } catch {
      // useMoveQueryToFolder already toasts the error.
    }
  };

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title="Move query to folder"
      sub={query?.name}
      size="sm"
      footer={
        <>
          <Button type="button" onClick={onClose} disabled={move.isPending}>Cancel</Button>
          <Button variant="primary" onClick={onSubmit} disabled={move.isPending}>
            {move.isPending ? 'Moving…' : 'Move'}
          </Button>
        </>
      }
    >
      <FolderPicker
        value={folderId}
        selectedLabel={folderLabel ?? undefined}
        onSelect={f => { setFolderId(f?.id ?? null); setFolderLabel(f?.path ?? null); }}
        clearLabel="No folder"
        ariaLabel="Destination folder"
      />
    </Dialog>
  );
}
