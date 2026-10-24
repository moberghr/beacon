/** Most tables the schema explorer renders at once; a warehouse can hold thousands, and each is several DOM rows. */
export const MAX_VISIBLE_TABLES = 300;

/** Most table names the SQL editor offers per completion request. */
export const MAX_TABLE_SUGGESTIONS = 300;

/**
 * How many tables of each schema to render. Collapsed schemas render none and spend none of the budget; expanded ones
 * are filled in order until `limit` tables are shown in total.
 */
export function visibleTableCounts(
  groups: ReadonlyArray<{ schema: string; tables: ReadonlyArray<unknown> }>,
  isCollapsed: (schema: string) => boolean,
  limit = MAX_VISIBLE_TABLES,
): Map<string, number> {
  const counts = new Map<string, number>();
  let remaining = limit;
  for (const group of groups) {
    if (isCollapsed(group.schema)) {
      counts.set(group.schema, 0);
      continue;
    }

    const shown = Math.min(group.tables.length, remaining);
    counts.set(group.schema, shown);
    remaining -= shown;
  }

  return counts;
}

/**
 * The tables worth offering for what the user has typed so far: those whose name or qualified name contains the typed
 * text, at most `limit` of them. An empty word keeps the first `limit` tables.
 */
export function pickCompletionTables<T extends { schemaName: string; tableName: string }>(
  tables: ReadonlyArray<T>,
  word: string,
  limit = MAX_TABLE_SUGGESTIONS,
): T[] {
  const term = word.trim().toLowerCase();
  const picked: T[] = [];
  for (const table of tables) {
    if (picked.length >= limit) {
      break;
    }

    if (term === '' || `${table.schemaName}.${table.tableName}`.toLowerCase().includes(term)) {
      picked.push(table);
    }
  }

  return picked;
}
