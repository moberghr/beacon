import { describe, expect, it } from 'vitest';
import { pickCompletionTables, visibleTableCounts } from './schemaLimits';

const group = (schema: string, count: number) => ({ schema, tables: Array.from({ length: count }, (_, i) => i) });

describe('visibleTableCounts', () => {
  it('shows every table while the total is under the limit', () => {
    const counts = visibleTableCounts([group('a', 3), group('b', 2)], () => false, 10);
    expect([...counts]).toEqual([['a', 3], ['b', 2]]);
  });

  it('spends the budget in order across expanded schemas', () => {
    const counts = visibleTableCounts([group('a', 6), group('b', 6), group('c', 6)], () => false, 8);
    expect([...counts]).toEqual([['a', 6], ['b', 2], ['c', 0]]);
  });

  it('lets collapsed schemas render nothing and spend nothing', () => {
    const counts = visibleTableCounts([group('a', 6), group('b', 6)], s => s === 'a', 8);
    expect([...counts]).toEqual([['a', 0], ['b', 6]]);
  });
});

describe('pickCompletionTables', () => {
  const tables = [
    { schemaName: 'sales', tableName: 'orders' },
    { schemaName: 'sales', tableName: 'customers' },
    { schemaName: 'hr', tableName: 'orders_archive' },
  ];

  it('keeps tables whose qualified name contains the typed text, case-insensitively', () => {
    expect(pickCompletionTables(tables, 'ORD').map(x => x.tableName)).toEqual(['orders', 'orders_archive']);
    expect(pickCompletionTables(tables, 'hr.').map(x => x.tableName)).toEqual(['orders_archive']);
  });

  it('caps the result at the limit and keeps the first tables for an empty word', () => {
    expect(pickCompletionTables(tables, '', 2).map(x => x.tableName)).toEqual(['orders', 'customers']);
  });
});
