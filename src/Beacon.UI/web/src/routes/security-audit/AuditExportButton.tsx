import { useState } from 'react';
import { Download } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/beacon';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { useAccess } from '@/auth/useAuth';
import { describeError } from '@/lib/api';
import { AUDIT_EXPORT_MAX_ROWS, collectAuditExport, downloadJson, type AuditExportPage } from '@/lib/auditExport';

interface AuditExportButtonProps<T> {
  /** One page of the window as filtered on screen, numbered from `firstPage`. */
  fetchPage: (page: number, pageSize: number) => Promise<AuditExportPage<T>>;
  /** The endpoint's first page number: 0 for the security audit, 1 for the MCP audit. */
  firstPage: number;
  /** What the file holds; shown in a confirmation before anything is read. */
  confirmMessage: string;
  fileName: string;
  disabled?: boolean;
}

/**
 * Downloads the window as filtered on screen as a JSON file, exactly as the server returned the rows (retention redaction
 * already applied). Only an Auditor sees it (an Admin holds the permission); the server refuses everyone else, and records
 * every page the export reads.
 */
export function AuditExportButton<T>({ fetchPage, firstPage, confirmMessage, fileName, disabled }: AuditExportButtonProps<T>) {
  const canExport = useAccess().hasAnyPermission('Auditor');
  const [busy, setBusy] = useState(false);
  const [confirming, setConfirming] = useState(false);

  if (!canExport) return null;

  async function run() {
    setConfirming(false);
    setBusy(true);
    try {
      const result = await collectAuditExport(fetchPage, firstPage);
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
    <>
      <Button type="button" size="sm" icon={<Download />} onClick={() => setConfirming(true)} disabled={disabled || busy} title="Reading the audit is recorded, page by page">
        {busy ? 'Exporting…' : 'Export JSON'}
      </Button>
      <ConfirmDialog
        open={confirming}
        title="Export JSON"
        message={confirmMessage}
        confirmLabel="Download"
        onConfirm={run}
        onCancel={() => setConfirming(false)}
      />
    </>
  );
}
