import { describe, expect, it } from 'vitest';
import { collectAuditExport } from './auditExport';

/** A fake server whose first page is `base`, serving ids newest first (id 0 is the newest event). */
function server(total: number, base: number) {
  const calls: number[] = [];
  const fetchPage = async (page: number, pageSize: number) => {
    calls.push(page);
    const start = (page - base) * pageSize;
    const items = Array.from({ length: Math.max(0, Math.min(pageSize, total - start)) }, (_, i) => start + i);
    return { items, totalCount: total };
  };
  return { calls, fetchPage };
}

describe('collectAuditExport', () => {
  it('reads every page of a one-based window and stops at the total', async () => {
    const { calls, fetchPage } = server(250, 1);
    const result = await collectAuditExport(fetchPage, 1, 5000, 100);

    expect(result.rows).toHaveLength(250);
    expect(result.rows[0]).toBe(0);
    expect(result.truncated).toBe(false);
    expect(calls).toEqual([1, 2, 3]);
  });

  it('starts at the first page of a zero-based window, so the newest event is in the file', async () => {
    const { calls, fetchPage } = server(250, 0);
    const result = await collectAuditExport(fetchPage, 0, 5000, 100);

    expect(result.rows).toHaveLength(250);
    expect(result.rows[0]).toBe(0);
    expect(result.rows[249]).toBe(249);
    expect(calls).toEqual([0, 1, 2]);
  });

  it('cuts a window larger than the cap and says so', async () => {
    const { calls, fetchPage } = server(1000, 1);
    const result = await collectAuditExport(fetchPage, 1, 300, 100);

    expect(result.rows).toHaveLength(300);
    expect(result.totalCount).toBe(1000);
    expect(result.truncated).toBe(true);
    expect(calls).toEqual([1, 2, 3]);
  });

  it('returns nothing for an empty window after one read', async () => {
    const { calls, fetchPage } = server(0, 0);
    const result = await collectAuditExport(fetchPage, 0);

    expect(result.rows).toEqual([]);
    expect(calls).toEqual([0]);
  });
});
