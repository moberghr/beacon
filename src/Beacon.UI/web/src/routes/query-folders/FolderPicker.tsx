import type { ReactNode } from 'react';
import { SearchPicker } from '@/components/data/SearchPicker';
import type { QueryFolderEntry } from './queries';

interface FolderPickerProps {
  value: number | null | undefined;
  selectedLabel?: ReactNode;
  onSelect: (folder: QueryFolderEntry | null) => void;
  clearLabel?: string;
  placeholder?: string;
  ariaLabel?: string;
}

/** Searchable folder picker over the paged folders endpoint; labels by tree path. */
export function FolderPicker({
  value,
  selectedLabel,
  onSelect,
  clearLabel,
  placeholder = 'Search folders…',
  ariaLabel = 'Folder',
}: FolderPickerProps) {
  return (
    <SearchPicker<QueryFolderEntry>
      path="/beacon/api/query-folders/paged"
      queryKey={['query-folders', 'picker']}
      value={value}
      selectedLabel={selectedLabel}
      getId={x => x.id}
      getLabel={x => x.path || x.name}
      onSelect={onSelect}
      placeholder={placeholder}
      noun="folders"
      clearLabel={clearLabel}
      ariaLabel={ariaLabel}
    />
  );
}
