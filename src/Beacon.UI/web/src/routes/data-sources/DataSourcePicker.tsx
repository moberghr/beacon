import type { ReactNode } from 'react';
import { SearchPicker } from '@/components/data/SearchPicker';
import { dataSourceEngine, useDataSourceQuery, type DataSourceEntry } from './queries';

interface DataSourcePickerProps {
  value: number | null | undefined;
  onSelect: (dataSource: DataSourceEntry | null) => void;
  /** Only sources with a database engine (SQL targets). */
  databaseOnly?: boolean;
  /**
   * True: only migration write targets (a migration's destination). Otherwise only the sources Beacon reads: a write
   * target is never queried, so no query, step, rule or actor can use one.
   */
  migrationTargets?: boolean;
  /** Offer "All data sources" (a filter) instead of forcing a choice. */
  clearLabel?: string;
  /** Known label for the current value, which skips the by-id lookup (e.g. the name stored on a step). */
  selectedLabel?: ReactNode;
  placeholder?: string;
  disabled?: boolean;
  hasError?: boolean;
  className?: string;
  ariaLabel?: string;
}

/** Searchable data source picker: fetches the first matches as the user types, never the whole list. */
export function DataSourcePicker({
  value,
  onSelect,
  databaseOnly,
  migrationTargets = false,
  clearLabel,
  selectedLabel,
  placeholder = 'Search data sources…',
  disabled,
  hasError,
  className,
  ariaLabel = 'Data source',
}: DataSourcePickerProps) {
  const selected = useDataSourceQuery(selectedLabel == null ? value : null);
  const label =
    selectedLabel ??
    (selected.data ? `${selected.data.name} (${dataSourceEngine(selected.data)})` : undefined);
  const params = {
    ...(databaseOnly ? { databaseOnly: true } : {}),
    migrationTargets,
  };

  return (
    <SearchPicker<DataSourceEntry>
      path="/beacon/api/data-sources"
      params={params}
      queryKey={['data-sources']}
      value={value}
      selectedLabel={label}
      getId={x => x.id}
      getLabel={x => x.name}
      getHint={dataSourceEngine}
      onSelect={onSelect}
      placeholder={placeholder}
      noun="data sources"
      clearLabel={clearLabel}
      disabled={disabled}
      hasError={hasError}
      className={className}
      ariaLabel={ariaLabel}
    />
  );
}
