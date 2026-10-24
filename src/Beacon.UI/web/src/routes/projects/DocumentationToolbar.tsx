import { useEffect, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Download, Sparkles } from 'lucide-react';
import { toast } from 'sonner';
import type { BackgroundTaskStatus, BackgroundTaskTicket } from '@/api/generated/beacon-api';
import { Button } from '@/components/beacon';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { describeError, httpErrorInfo } from '@/lib/api';
import { BackgroundTaskState } from '@/lib/enums';
import { isBackgroundTaskFinished } from '@/lib/useBackgroundTask';
import {
  downloadProjectDocumentation,
  useProjectDocumentationGenerationStatus,
  useStartProjectDocumentationGeneration,
} from './queries';

interface Props {
  projectId: number;
  /** The latest generated documentation, when there is one: what Export downloads. */
  latestId: number | null;
  /** Curate in the project: regenerating replaces what every caller's prompts are grounded on. */
  canGenerate: boolean;
}

/**
 * Generate and Export for a project's documentation. Generating runs as a background task that is followed until it
 * finishes (the documentation is read again then); exporting downloads the latest documentation as a Markdown or HTML file.
 */
export function DocumentationToolbar({ projectId, latestId, canGenerate }: Props) {
  const queryClient = useQueryClient();
  const start = useStartProjectDocumentationGeneration();
  const [ticket, setTicket] = useState<BackgroundTaskTicket | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [exporting, setExporting] = useState<'md' | 'html' | null>(null);
  const task = useProjectDocumentationGenerationStatus(projectId, ticket);

  const finished = isBackgroundTaskFinished(task.status?.state) ? task.status : null;
  const readFailed = ticket != null && task.error != null;

  useEffect(() => {
    if (ticket == null || (finished == null && !readFailed)) {
      return;
    }
    if (finished?.state === BackgroundTaskState.Succeeded) {
      void queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'documentation'] });
      void queryClient.invalidateQueries({ queryKey: ['projects', projectId] });
      toast.success('Documentation generated');
    } else if (finished != null) {
      toast.error(describeGenerationFailure(finished));
    } else {
      toast.error(describeError(task.error, 'Could not read the generation progress'));
    }
    setTicket(null);
  }, [ticket, finished, readFailed, task.error, projectId, queryClient]);

  const generating = ticket != null || start.isPending;
  const generateLabel = !generating
    ? latestId == null ? 'Generate documentation' : 'Regenerate documentation'
    : task.state === BackgroundTaskState.Running
      ? 'Generating documentation…'
      : 'Generation queued…';

  async function generate() {
    setConfirming(false);
    try {
      setTicket(await start.mutateAsync(projectId));
    } catch (err) {
      // 429: a generation of the project is already queued or running.
      if (httpErrorInfo(err)?.status === 429) {
        toast.warning(describeError(err, 'The documentation of this project is already being generated.'));
      } else {
        toast.error(describeError(err, 'Could not start the generation'));
      }
    }
  }

  async function download(asHtml: boolean) {
    if (latestId == null) {
      return;
    }
    setExporting(asHtml ? 'html' : 'md');
    try {
      const name = await downloadProjectDocumentation(latestId, asHtml);
      toast.success(`Exported ${name}`);
    } catch (err) {
      toast.error(describeError(err, 'Export failed'));
    } finally {
      setExporting(null);
    }
  }

  if (!canGenerate && latestId == null) {
    return null;
  }

  return (
    <>
      <div className="flex flex-wrap items-center gap-2 mb-4">
        {canGenerate && (
          <Button
            type="button"
            variant="primary"
            icon={<Sparkles className={generating ? 'animate-pulse' : undefined} />}
            onClick={() => (latestId == null ? void generate() : setConfirming(true))}
            disabled={generating}
            aria-busy={generating}
          >
            {generateLabel}
          </Button>
        )}
        {latestId != null && (
          <>
            <Button type="button" icon={<Download />} onClick={() => void download(false)} disabled={exporting != null}>
              {exporting === 'md' ? 'Exporting…' : 'Export Markdown'}
            </Button>
            <Button type="button" icon={<Download />} onClick={() => void download(true)} disabled={exporting != null}>
              {exporting === 'html' ? 'Exporting…' : 'Export HTML'}
            </Button>
          </>
        )}
      </div>

      <ConfirmDialog
        open={confirming}
        title="Regenerate documentation?"
        message="A new version is generated from the project's current metadata and code. It becomes the documentation every caller's prompts are grounded on; the earlier versions stay in the history, but sections you edited by hand are not carried over."
        confirmLabel="Regenerate"
        onConfirm={() => void generate()}
        onCancel={() => setConfirming(false)}
      />
    </>
  );
}

// A failed generation says why by its code; the server log has the rest.
function describeGenerationFailure(status: BackgroundTaskStatus): string {
  switch (status.failureCode) {
    case 'busy':
      return 'The documentation of this project was already being generated; try again when it has finished.';
    case 'forbidden':
      return 'You can no longer curate this project or query its data sources, so its documentation was not generated.';
    case 'not_found':
      return 'This project was not found.';
    case 'abandoned':
      return 'The generation stopped without finishing; try again.';
    case 'cancelled':
      return 'The generation was cancelled.';
    default:
      return 'Documentation generation failed; the server log has the details.';
  }
}
