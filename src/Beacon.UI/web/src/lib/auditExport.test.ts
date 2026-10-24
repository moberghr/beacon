import { describe, expect, it } from 'vitest';
import { collectAuditExport } from './auditExport';

function server(total: number) {
  const calls: number[] = [];
  const fetchPage = async (page: number, pageSize: number) => {
    calls.push(page);
    const start = (page - 1) * pageSize;
    const items = Array.from({ length: Math.max(0, Math.min(pageSize, total - start)) }, (_, i) => start + i);
    return { items, totalCount: total };
  };
  return { calls, fetchPage };
}

describe('collectAuditExport', () => {
  it('reads every page of a window and stops at the total', async () => {
    const { calls, fetchPage } = server(250);
    const result = await collectAuditExport(fetchPage, 5000, 100);

    expect(result.rows).toHaveLength(250);
    expect(result.truncated).toBe(false);
    expect(calls).toEqual([1, 2, 3]);
  });

  it('cuts a window larger than the cap and says so', async () => {
    const { calls, fetchPage } = server(1000);
    const result = await collectAuditExport(fetchPage, 300, 100);

    expect(result.rows).toHaveLength(300);
    expect(result.totalCount).toBe(1000);
    expect(result.truncated).toBe(true);
    expect(calls).toEqual([1, 2, 3]);
  });

  it('returns nothing for an empty window after one read', async () => {
    const { calls, fetchPage } = server(0);
    const result = await collectAuditExport(fetchPage);

    expect(result.rows).toEqual([]);
    expect(calls).toEqual([1]);
  });
});
