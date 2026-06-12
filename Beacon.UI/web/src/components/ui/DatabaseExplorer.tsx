import { useEffect, useId, useMemo, useRef, useState } from 'react';
import { ChevronDown, Database, Box } from 'lucide-react';
import { Input } from '@/components/beacon';
import { cn } from '@/lib/cn';
import {
  useDataSourceMetadataQuery,
  type TableMetadataDto,
} from '@/routes/data-sources/queries';

export interface DatabaseExplorerProps {
  dataSourceId: number | null | undefined;
  /** Called with the text to insert at the cursor (e.g. `public.users`). */
  onInsert: (text: string) => void;
  className?: string;
}

interface SchemaGroup {
  schema: string;
  tables: TableMetadataDto[];
}

function groupBySchema(tables: TableMetadataDto[]): SchemaGroup[] {
  const map = new Map<string, TableMetadataDto[]>();
  for (const t of tables) {
    const key = t.schemaName || 'default';
    const list = map.get(key);
    if (list) {
      list.push(t);
    } else {
      map.set(key, [t]);
    }
  }
  return Array.from(map.entries())
    .map(([schema, ts]) => ({
      schema,
      tables: ts.sort((a, b) => a.tableName.localeCompare(b.tableName)),
    }))
    .sort((a, b) => a.schema.localeCompare(b.schema));
}

const rowBase =
  'flex items-center gap-1.5 px-2 py-1 text-xs cursor-pointer select-none hover:bg-surface-2';

const EMPTY_TABLES: TableMetadataDto[] = [];

/**
 * Read-only schema/tables panel rendered to the LEFT of a Monaco editor.
 * Click a table → inserts `<schema>.<table>` at the editor cursor via
 * `onInsert`. Click a column → inserts the column name.
 */
export function DatabaseExplorer({ dataSourceId, onInsert, className }: DatabaseExplorerProps) {
  const [collapsedSchemas, setCollapsedSchemas] = useState<Set<string>>(new Set());
  const [expandedTables, setExpandedTables] = useState<Set<string>>(new Set());
  const [search, setSearch] = useState('');
  const idPrefix = useId();

  const query = useDataSourceMetadataQuery(dataSourceId);
  const tables = query.data?.tables ?? EMPTY_TABLES;

  const filtered = useMemo(() => {
    const term = search.trim().toLowerCase();
    if (!term) return tables;
    return tables.filter(
      t =>
        t.tableName.toLowerCase().includes(term) ||
        t.schemaName.toLowerCase().includes(term),
    );
  }, [tables, search]);

  const groups = useMemo(() => groupBySchema(filtered), [filtered]);

  // Re-initialize per data source: collapse all schemas and clear expanded
  // tables whenever metadata for a new dataSourceId arrives.
  const initializedForRef = useRef<number | null>(null);
  useEffect(() => {
    if (dataSourceId == null || initializedForRef.current === dataSourceId) return;
    if (tables.length === 0) return;
    const schemas = new Set<string>();
    for (const t of tables) {
      schemas.add(t.schemaName || 'default');
    }
    setCollapsedSchemas(schemas);
    setExpandedTables(new Set());
    initializedForRef.current = dataSourceId;
  }, [dataSourceId, tables]);

  if (dataSourceId == null || dataSourceId <= 0) {
    return null;
  }

  const tableCount = tables.length;

  const toggleSchema = (schema: string) => {
    setCollapsedSchemas(prev => {
      const next = new Set(prev);
      if (next.has(schema)) {
        next.delete(schema);
      } else {
        next.add(schema);
      }
      return next;
    });
  };

  const toggleTable = (key: string) => {
    setExpandedTables(prev => {
      const next = new Set(prev);
      if (next.has(key)) {
        next.delete(key);
      } else {
        next.add(key);
      }
      return next;
    });
  };

  const message = (text: string) => (
    <div className="px-2 py-2 text-xs text-text-muted">{text}</div>
  );

  return (
    <aside
      className={cn(
        'flex flex-col bg-surface border border-border rounded-md overflow-hidden text-xs',
        className,
      )}
    >
      <div className="flex items-center gap-1.5 px-2 py-1.5 border-b border-border bg-surface-2 text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">
        <Database size={13} />
        <span>Database explorer</span>
        <span className="ml-auto mono normal-case tracking-normal text-text-subtle">
          {query.isLoading ? '…' : `${tableCount} table${tableCount === 1 ? '' : 's'}`}
        </span>
      </div>
      <div className="p-1.5 border-b border-border">
        <Input
          type="search"
          placeholder="Filter tables…"
          value={search}
          onChange={e => setSearch(e.target.value)}
          className="text-xs px-2 py-1"
        />
      </div>

      <div className="flex-1 overflow-y-auto py-1">
        {query.isLoading && message('Loading metadata…')}
        {query.isError && message("Couldn't load schema (not connected?)")}
        {!query.isLoading &&
          !query.isError &&
          groups.length === 0 &&
          message(search ? 'No tables match.' : 'No tables.')}

        {!query.isLoading && !query.isError && groups.length > 0 && (
          <div>
            {groups.map(group => {
              // While a filter is active, auto-expand matching schemas without
              // touching the user's persisted collapse state.
              const searchActive = search.trim().length > 0;
              const collapsed = !searchActive && collapsedSchemas.has(group.schema);
              const schemaPanelId = `${idPrefix}-schema-${group.schema}`;
              return (
                <div key={group.schema} className="mt-1 first:mt-0">
                  <button
                    type="button"
                    className={cn(rowBase, 'w-full text-left')}
                    aria-expanded={!collapsed}
                    aria-controls={collapsed ? undefined : schemaPanelId}
                    onClick={() => toggleSchema(group.schema)}
                  >
                    <ChevronDown
                      size={11}
                      className={cn('transition-transform', collapsed && '-rotate-90')}
                    />
                    <span className="size-2 rounded-xs bg-brand-500" />
                    <span className="font-medium">{group.schema}</span>
                    <span className="ml-auto text-text-subtle mono">
                      {group.tables.length}
                    </span>
                  </button>
                  {!collapsed && (
                    <div id={schemaPanelId}>
                    {group.tables.map(table => {
                      const fqn = `${table.schemaName}.${table.tableName}`;
                      const expanded = expandedTables.has(fqn);
                      const columnsPanelId = `${idPrefix}-cols-${fqn}`;
                      const tooltip =
                        table.columns
                          .slice(0, 3)
                          .map(c => `${c.columnName} (${c.dataType})`)
                          .join('\n') || 'no columns';
                      return (
                        <div key={fqn}>
                          <div className="flex items-center pl-5 pr-2 hover:bg-surface-2">
                            <button
                              type="button"
                              aria-expanded={expanded}
                              aria-controls={expanded ? columnsPanelId : undefined}
                              aria-label={
                                expanded
                                  ? `Collapse ${table.tableName} columns`
                                  : `Expand ${table.tableName} columns`
                              }
                              onClick={() => toggleTable(fqn)}
                              className="inline-flex shrink-0 py-1 text-text-muted hover:text-text"
                            >
                              <ChevronDown
                                size={9}
                                className={cn('transition-transform', !expanded && '-rotate-90')}
                              />
                            </button>
                            <button
                              type="button"
                              title={tooltip}
                              onClick={() => onInsert(fqn)}
                              onDoubleClick={() => toggleTable(fqn)}
                              className="flex-1 min-w-0 flex items-center gap-1.5 px-1.5 py-1 text-xs cursor-pointer select-none text-left"
                            >
                              <Box size={10} className="text-text-muted" />
                              <span>{table.tableName}</span>
                              <span className="ml-auto mono text-text-subtle">
                                {table.columns.length}
                              </span>
                            </button>
                          </div>
                          {expanded && (
                            <div id={columnsPanelId}>
                            {table.columns.map(col => (
                              <div
                                key={col.columnName}
                                className={cn(rowBase, 'pl-9')}
                                role="button"
                                tabIndex={0}
                                onClick={() => onInsert(col.columnName)}
                                onKeyDown={e => {
                                  if (e.key === 'Enter' || e.key === ' ') {
                                    e.preventDefault();
                                    onInsert(col.columnName);
                                  }
                                }}
                                title={`${col.dataType}${col.isPrimaryKey ? ' · PK' : ''}${col.isNullable ? '' : ' · NOT NULL'}`}
                              >
                                <span className="mono">{col.columnName}</span>
                                <span className="ml-auto mono text-text-subtle text-[10px]">
                                  {col.dataType}
                                </span>
                              </div>
                            ))}
                            </div>
                          )}
                        </div>
                      );
                    })}
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        )}
      </div>
    </aside>
  );
}

export default DatabaseExplorer;
