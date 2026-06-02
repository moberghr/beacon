import { useEffect, useMemo, useRef, useState } from 'react';
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

/**
 * Read-only schema/tables panel rendered to the LEFT of a Monaco editor.
 * Click a table → inserts `<schema>.<table>` at the editor cursor via
 * `onInsert`. Click a column → inserts the column name.
 */
export function DatabaseExplorer({ dataSourceId, onInsert, className }: DatabaseExplorerProps) {
  const [collapsedSchemas, setCollapsedSchemas] = useState<Set<string>>(new Set());
  const [expandedTables, setExpandedTables] = useState<Set<string>>(new Set());
  const [search, setSearch] = useState('');

  const query = useDataSourceMetadataQuery(dataSourceId);
  const tables = query.data?.tables ?? [];

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

  const initializedRef = useRef(false);
  useEffect(() => {
    if (initializedRef.current) return;
    if (tables.length === 0) return;
    const schemas = new Set<string>();
    for (const t of tables) {
      schemas.add(t.schemaName || 'default');
    }
    setCollapsedSchemas(schemas);
    initializedRef.current = true;
  }, [tables]);

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
              const collapsed = collapsedSchemas.has(group.schema);
              return (
                <div key={group.schema} className="mt-1 first:mt-0">
                  <div
                    className={rowBase}
                    role="button"
                    tabIndex={0}
                    aria-expanded={!collapsed}
                    onClick={() => toggleSchema(group.schema)}
                    onKeyDown={e => {
                      if (e.key === 'Enter' || e.key === ' ') {
                        e.preventDefault();
                        toggleSchema(group.schema);
                      }
                    }}
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
                  </div>
                  {!collapsed &&
                    group.tables.map(table => {
                      const fqn = `${table.schemaName}.${table.tableName}`;
                      const expanded = expandedTables.has(fqn);
                      const tooltip =
                        table.columns
                          .slice(0, 3)
                          .map(c => `${c.columnName} (${c.dataType})`)
                          .join('\n') || 'no columns';
                      return (
                        <div key={fqn}>
                          <div
                            className={cn(rowBase, 'pl-5')}
                            title={tooltip}
                            role="button"
                            tabIndex={0}
                            onClick={() => onInsert(fqn)}
                            onDoubleClick={() => toggleTable(fqn)}
                            onKeyDown={e => {
                              if (e.key === 'Enter') {
                                e.preventDefault();
                                onInsert(fqn);
                              } else if (e.key === ' ') {
                                e.preventDefault();
                                toggleTable(fqn);
                              }
                            }}
                          >
                            <span
                              role="button"
                              tabIndex={0}
                              aria-label={expanded ? 'Collapse table columns' : 'Expand table columns'}
                              onClick={e => {
                                e.stopPropagation();
                                toggleTable(fqn);
                              }}
                              onKeyDown={e => {
                                if (e.key === 'Enter' || e.key === ' ') {
                                  e.stopPropagation();
                                  e.preventDefault();
                                  toggleTable(fqn);
                                }
                              }}
                              className="inline-flex"
                            >
                              <ChevronDown
                                size={9}
                                className={cn('transition-transform', !expanded && '-rotate-90')}
                              />
                            </span>
                            <Box size={10} className="text-text-muted" />
                            <span>{table.tableName}</span>
                            <span className="ml-auto mono text-text-subtle">
                              {table.columns.length}
                            </span>
                          </div>
                          {expanded &&
                            table.columns.map(col => (
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
                      );
                    })}
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
