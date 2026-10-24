import { useState } from 'react';
import { Download } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/beacon';
import { useAccess } from '@/auth/useAuth';
import { describeError } from '@/lib/api';
import { AUDIT_EXPORT_MAX_ROWS, collectAuditExport, downloadJson, type AuditExportPage } from '@/lib/auditExport';

interface AuditExportButtonProps<T> {
  /** One page (1-based) of the window as filtered on screen. */
  fetchPage: (page: number, pageSize: number) => Promise<AuditExportPage<T>>;
  fileName: string;
  disabled?: boolean;
}

/**
 * Downloads the window as filtered on screen as a JSON file, exactly as the server returned the rows (retention redaction
 * already applied). Only an Auditor sees it (an Admin holds the permission); the server refuses everyone else, and records
 * every page the export reads.
 */
export function AuditExportButton<T>({ fetchPage, fileName, disabled }: AuditExportButtonProps<T>) {
  const canExport = useAccess().hasAnyPermission('Auditor');
  const [busy, setBusy] = useState(false);

  if (!canExport) return null;

  async function run() {
    setBusy(true);
    try {
      const result = await collectAuditExport(fetchPage);
      downloadJson(fileName, result.rows);
      if (result.truncated) {
        toast.warning(`The window holds ${result.totalCount} rows; the first ${AUDIT_EXPORT_MAX_ROWS} were exported. Narrow the window to get the rest.`);
      } else {
        toast.success(`Exported ${result.rows.length} ${result.rows.length === 1 ? 'row' : 'rows'}.`);
      }
    } catch (err) {
      toast.error(describeError(err, 'The export failed'));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Button type="button" size="sm" icon={<Download />} onClick={run} disabled={disabled || busy} title="Reading the audit is recorded, page by page">
      {busy ? 'Exporting…' : 'Export JSON'}
    </Button>
  );
}
