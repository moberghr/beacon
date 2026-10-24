import { useEffect, useState } from 'react';
import { toast } from 'sonner';
import { Dialog } from '@/components/ui/Dialog';
import { Button, Field, Input } from '@/components/beacon';
import { FolderPicker } from './FolderPicker';
import { useCreateQueryFolder, useUpdateQueryFolder, type QueryFolderEntry } from './queries';

interface QueryFolderDialogProps {
  open: boolean;
  onClose: () => void;
  /** The folder to rename; null creates a new one. */
  folder?: QueryFolderEntry | null;
}

/** Create a folder (name, description, parent) or rename one (name, description; a folder keeps its parent). */
export function QueryFolderDialog({ open, onClose, folder }: QueryFolderDialogProps) {
  const isEdit = folder != null;
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [parentId, setParentId] = useState<number | null>(null);
  const [touched, setTouched] = useState(false);
  const createMutation = useCreateQueryFolder();
  const updateMutation = useUpdateQueryFolder();
  const busy = createMutation.isPending || updateMutation.isPending;

  useEffect(() => {
    if (!open) return;
    setName(folder?.name ?? '');
    setDescription(folder?.description ?? '');
    setParentId(null);
    setTouched(false);
  }, [open, folder]);

  const nameError = touched && name.trim() === '' ? 'Name is required' : null;

  const onSubmit = async () => {
    setTouched(true);
    const trimmed = name.trim();
    if (trimmed === '') return;
    const desc = description.trim() === '' ? null : description.trim();
    try {
      if (isEdit) {
        await updateMutation.mutateAsync({ id: folder.id, name: trimmed, description: desc });
        toast.success(`Updated folder '${trimmed}'`);
      } else {
        await createMutation.mutateAsync({ name: trimmed, description: desc, parentFolderId: parentId });
        toast.success(`Created folder '${trimmed}'`);
      }
      onClose();
    } catch {
      // The mutation hooks already toast the error.
    }
  };

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={isEdit ? 'Rename folder' : 'Add a folder'}
      sub="Folders group saved queries. They are not tied to a project."
      size="md"
      footer={
        <>
          <Button type="button" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button type="submit" form="query-folder-form" variant="primary" disabled={busy}>
            {busy ? 'Saving…' : isEdit ? 'Save changes' : 'Create folder'}
          </Button>
        </>
      }
    >
      <form
        id="query-folder-form"
        noValidate
        className="flex flex-col gap-3.5"
        onSubmit={e => { e.preventDefault(); void onSubmit(); }}
      >
        <Field label={<>Name <span className="text-crit">*</span></>} htmlFor="query-folder-name">
          <Input
            id="query-folder-name"
            autoComplete="off"
            maxLength={200}
            aria-invalid={!!nameError}
            value={name}
            onChange={e => setName(e.target.value)}
          />
          {nameError && <span className="text-xs text-crit">{nameError}</span>}
        </Field>
        <Field label="Description" htmlFor="query-folder-description">
          <Input
            id="query-folder-description"
            placeholder="Optional"
            maxLength={500}
            value={description}
            onChange={e => setDescription(e.target.value)}
          />
        </Field>
        {!isEdit && (
          <div className="flex flex-col gap-1.5">
            <span className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">Parent folder</span>
            <FolderPicker
              value={parentId}
              onSelect={f => setParentId(f?.id ?? null)}
              clearLabel="No parent (top level)"
              ariaLabel="Parent folder"
            />
          </div>
        )}
      </form>
    </Dialog>
  );
}
