import { useEffect, useState } from 'react';
import { Button, Modal, ModalBody, ModalFooter, ModalHeader } from '@/components/beacon';
import { SearchMultiSelect } from '@/components/data/SearchMultiSelect';
import type { DataSourceEntry } from '@/routes/data-sources/queries';
import { useLinkProjectDataSources } from './queries';

interface LinkDataSourcesModalProps {
  open: boolean;
  projectId: number;
  projectName: string;
  /** Sources the project already links: shown checked and locked. */
  linkedDataSourceIds: number[];
  onClose: () => void;
}

/**
 * Links data sources into a project. The server decides what may be linked: an Admin links any source, anyone else
 * only sources already in an open project; a refused link is reported, nothing is linked partially.
 */
export function LinkDataSourcesModal({ open, projectId, projectName, linkedDataSourceIds, onClose }: LinkDataSourcesModalProps) {
  const [selected, setSelected] = useState<DataSourceEntry[]>([]);
  const link = useLinkProjectDataSources(projectId);

  useEffect(() => {
    if (open) {
      setSelected([]);
    }
  }, [open]);

  const onSubmit = async () => {
    try {
      await link.mutateAsync(selected.map(x => x.id));
      onClose();
    } catch {
      // Error toast already raised by the link mutation hook.
    }
  };

  return (
    <Modal open={open} onClose={onClose} width={560} ariaLabel={`Link data sources to ${projectName}`}>
      <ModalHeader
        variant="nodes"
        emphasis="Link data sources"
        sub={`Members of ${projectName} can use the linked sources. Only Admins can link a source that is not in an open project yet.`}
        onClose={onClose}
      />
      <ModalBody>
        <SearchMultiSelect<DataSourceEntry>
          path="/beacon/api/data-sources"
          queryKey={['data-sources']}
          selected={selected}
          onChange={setSelected}
          getId={x => x.id}
          getLabel={x => x.name}
          renderItem={x => (
            <span className="flex items-center justify-between gap-2 w-full">
              <span className="truncate">{x.name}</span>
              <span className="text-xs text-text-muted mono">{x.databaseEngineType ?? x.dataSourceType}</span>
            </span>
          )}
          lockedIds={linkedDataSourceIds}
          noun="data sources"
        />
      </ModalBody>
      <ModalFooter>
        <Button type="button" onClick={onClose}>Cancel</Button>
        <Button
          variant="primary"
          type="button"
          disabled={selected.length === 0 || link.isPending}
          onClick={onSubmit}
        >
          {selected.length > 1 ? `Link ${selected.length} data sources` : 'Link data source'}
        </Button>
      </ModalFooter>
    </Modal>
  );
}
