/** The most rows one export reads; a larger window is cut here and the Auditor is told. */
export const AUDIT_EXPORT_MAX_ROWS = 5000;
/** Page size of the export's reads (the server's list maximum). */
export const AUDIT_EXPORT_PAGE_SIZE = 100;

export interface AuditExportPage<T> {
  items: T[];
  totalCount: number;
}

export interface AuditExportResult<T> {
  rows: T[];
  totalCount: number;
  /** True when the window holds more rows than {@link AUDIT_EXPORT_MAX_ROWS}. */
  truncated: boolean;
}

/**
 * Reads a window page by page (1-based pages, as both audit endpoints take) until it is whole or the cap is reached.
 * Every page read is itself recorded by the server, so the export is as visible as reading the list.
 */
export async function collectAuditExport<T>(
  fetchPage: (page: number, pageSize: number) => Promise<AuditExportPage<T>>,
  maxRows: number = AUDIT_EXPORT_MAX_ROWS,
  pageSize: number = AUDIT_EXPORT_PAGE_SIZE,
): Promise<AuditExportResult<T>> {
  const rows: T[] = [];
  let totalCount = 0;
  for (let page = 1; rows.length < maxRows; page++) {
    const result = await fetchPage(page, pageSize);
    totalCount = result.totalCount;
    rows.push(...result.items);
    if (result.items.length === 0 || rows.length >= totalCount) break;
  }

  return { rows: rows.slice(0, maxRows), totalCount, truncated: totalCount > maxRows };
}

/** Saves `rows` as a JSON file in the browser, exactly as the server returned them. */
export function downloadJson(fileName: string, rows: unknown[]): void {
  const url = URL.createObjectURL(new Blob([JSON.stringify(rows, null, 2)], { type: 'application/json' }));
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}
