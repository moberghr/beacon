import { useMemo, useState } from 'react';
import { Icon } from '@/components/Icon';
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
    .map(([schema, ts]) => ({ schema, tables: ts.sort((a, b) => a.tableName.localeCompare(b.tableName)) }))
    .sort((a, b) => a.schema.localeCompare(b.schema));
}

/**
 * Read-only schema/tables panel rendered to the LEFT of a Monaco editor.
 * Click a table → inserts `<schema>.<table>` at the editor cursor via
 * the supplied `onInsert` callback. Click a column (after expanding a
 * table) → inserts the column name plain.
 *
 * Loading state shows a small skeleton; error state stays non-fatal —
 * pages render fine without metadata. Search filters tables across all
 * schemas case-insensitively.
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
    return tables.filter(t =>
      t.tableName.toLowerCase().includes(term) ||
      t.schemaName.toLowerCase().includes(term),
    );
  }, [tables, search]);

  const groups = useMemo(() => groupBySchema(filtered), [filtered]);

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

  return (
    <aside className={'sql__sidebar' + (className ? ' ' + className : '')}>
      <div className="sql__sidebar-head">
        <Icon.Database size={13} />
        <span>Database explorer</span>
        <span className="mono">
          {query.isLoading ? '…' : `${tableCount} table${tableCount === 1 ? '' : 's'}`}
        </span>
      </div>
      <div style={{ padding: '6px 8px', borderBottom: '1px solid var(--border)' }}>
        <input
          type="search"
          className="q-input"
          placeholder="Filter tables…"
          value={search}
          onChange={e => setSearch(e.target.value)}
          style={{ fontSize: 11.5, padding: '4px 8px' }}
        />
      </div>

      {query.isLoading && (
        <div className="sql__schemas">
          <div className="muted" style={{ fontSize: 11.5, padding: 8 }}>Loading metadata…</div>
        </div>
      )}

      {query.isError && (
        <div className="sql__schemas">
          <div className="muted" style={{ fontSize: 11.5, padding: 8 }}>
            Couldn't load schema (not connected?)
          </div>
        </div>
      )}

      {!query.isLoading && !query.isError && groups.length === 0 && (
        <div className="sql__schemas">
          <div className="muted" style={{ fontSize: 11.5, padding: 8 }}>
            {search ? 'No tables match.' : 'No tables.'}
          </div>
        </div>
      )}

      {!query.isLoading && !query.isError && groups.length > 0 && (
        <div className="sql__schemas">
          {groups.map(group => {
            const collapsed = collapsedSchemas.has(group.schema);
            return (
              <div key={group.schema} className="sql__schema">
                <div
                  className="sql__schema-row"
                  onClick={() => toggleSchema(group.schema)}
                  style={{ marginTop: 4 }}
                >
                  <span style={{ display: 'inline-flex', transform: collapsed ? 'rotate(-90deg)' : 'none' }}>
                    <Icon.ChevronDown size={11} />
                  </span>
                  <span
                    style={{
                      width: 9,
                      height: 9,
                      borderRadius: 2,
                      background: 'var(--brand-500)',
                    }}
                  />
                  <span>{group.schema}</span>
                  <span className="subtle">
                    {group.tables.length} table{group.tables.length === 1 ? '' : 's'}
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
                          className="sql__table-row"
                          title={tooltip}
                          onClick={() => onInsert(fqn)}
                          onDoubleClick={() => toggleTable(fqn)}
                        >
                          <span
                            onClick={e => {
                              e.stopPropagation();
                              toggleTable(fqn);
                            }}
                            style={{ display: 'inline-flex', cursor: 'pointer' }}
                          >
                            <span style={{ display: 'inline-flex', transform: expanded ? 'none' : 'rotate(-90deg)' }}>
                              <Icon.ChevronDown size={9} />
                            </span>
                          </span>
                          <Icon.Box size={10} />
                          <span>{table.tableName}</span>
                          <span style={{ marginLeft: 'auto' }} className="mono subtle">
                            {table.columns.length}
                          </span>
                        </div>
                        {expanded &&
                          table.columns.map(col => (
                            <div
                              key={col.columnName}
                              className="sql__column-row"
                              onClick={() => onInsert(col.columnName)}
                              title={`${col.dataType}${col.isPrimaryKey ? ' · PK' : ''}${col.isNullable ? '' : ' · NOT NULL'}`}
                            >
                              <span className="mono">{col.columnName}</span>
                              <span className="subtle mono" style={{ marginLeft: 'auto', fontSize: 10 }}>
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
    </aside>
  );
}

export default DatabaseExplorer;
