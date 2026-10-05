import type { ReactNode } from 'react';
import { SearchPicker } from '@/components/data/SearchPicker';
import { dataSourceEngine, useDataSourceQuery, type DataSourceEntry } from './queries';

interface DataSourcePickerProps {
  value: number | null | undefined;
  onSelect: (dataSource: DataSourceEntry | null) => void;
  /** Only sources with a database engine (SQL targets). */
  databaseOnly?: boolean;
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

  return (
    <SearchPicker<DataSourceEntry>
      path="/beacon/api/data-sources"
      params={databaseOnly ? { databaseOnly: true } : undefined}
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
