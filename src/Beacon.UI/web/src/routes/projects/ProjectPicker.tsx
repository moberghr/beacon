import type { ReactNode } from 'react';
import { SearchPicker } from '@/components/data/SearchPicker';
import type { ProjectSummaryEntry } from './queries';

interface ProjectPickerProps {
  value: number | null | undefined;
  onSelect: (project: ProjectSummaryEntry | null) => void;
  /** Label for the "no project" choice, e.g. "Global defaults"; omit to force a choice. */
  clearLabel?: string;
  /** Known label for the current value (pages that picked it earlier keep the name). */
  selectedLabel?: ReactNode;
  placeholder?: string;
  className?: string;
  ariaLabel?: string;
}

/** Searchable project picker: fetches the first matches as the user types, never the whole list. */
export function ProjectPicker({
  value,
  onSelect,
  clearLabel,
  selectedLabel,
  placeholder = 'Search projects…',
  className,
  ariaLabel = 'Project',
}: ProjectPickerProps) {
  return (
    <SearchPicker<ProjectSummaryEntry>
      path="/beacon/api/projects"
      queryKey={['projects']}
      value={value}
      selectedLabel={selectedLabel}
      getId={x => x.id}
      getLabel={x => x.name}
      onSelect={onSelect}
      placeholder={placeholder}
      noun="projects"
      clearLabel={clearLabel}
      className={className}
      ariaLabel={ariaLabel}
    />
  );
}
