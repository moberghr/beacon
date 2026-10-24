import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Archive, Sparkles } from 'lucide-react';
import type { BackgroundTaskStatus, BackgroundTaskTicket } from '@/api/generated/beacon-api';
import { Button } from '@/components/beacon';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { describeError } from '@/lib/api';
import { AiActorStatus, BackgroundTaskState } from '@/lib/enums';
import { isBackgroundTaskFinished } from '@/lib/useBackgroundTask';
import { useAiActorThinkStatus, useArchiveAiActor, useStartAiActorThink } from './queries';

interface AiActorActionsProps {
  actorId: number;
  actorName: string;
  status: AiActorStatus;
}

/**
 * The actor's "Think now" and "Archive" buttons. Think now starts a background think cycle and follows it to its end;
 * Archive asks first. Both are the creator's or an Admin's: anyone else gets the server's refusal as a toast. An archived
 * actor offers neither, and only an active one thinks.
 */
export function AiActorActions({ actorId, actorName, status }: AiActorActionsProps) {
  if (status === AiActorStatus.Archived) {
    return null;
  }

  return (
    <>
      {status === AiActorStatus.Active && <ThinkNowButton actorId={actorId} />}
      <ArchiveButton actorId={actorId} actorName={actorName} />
    </>
  );
}

function ThinkNowButton({ actorId }: { actorId: number }) {
  const queryClient = useQueryClient();
  const start = useStartAiActorThink(actorId);
  const [ticket, setTicket] = useState<BackgroundTaskTicket | null>(null);
  const task = useAiActorThinkStatus(actorId, ticket);

  const finishedStatus = isBackgroundTaskFinished(task.status?.state) ? task.status : null;
  const readFailed = ticket != null && task.error != null;

  useEffect(() => {
    if (ticket == null || (finishedStatus == null && !readFailed)) {
      return;
    }
    // The cycle recorded an execution either way: the page shows it with the actor's other cycles.
    void queryClient.invalidateQueries({ queryKey: ['ai-actor', actorId] });
    void queryClient.invalidateQueries({ queryKey: ['ai-actors'] });
    if (finishedStatus?.state === BackgroundTaskState.Succeeded) {
      toast.success('Think cycle finished');
    } else if (finishedStatus != null) {
      const describe = describeThinkOutcome(finishedStatus);
      if (finishedStatus.state === BackgroundTaskState.Skipped) {
        toast.warning(describe);
      } else {
        toast.error(describe);
      }
    } else {
      toast.error(describeError(task.error, 'Could not read the think cycle progress'));
    }
    setTicket(null);
  }, [ticket, finishedStatus, readFailed, task.error, actorId, queryClient]);

  const thinking = ticket != null || start.isPending;
  const label = !thinking
    ? 'Think now'
    : task.state === BackgroundTaskState.Running
      ? 'Thinking…'
      : 'Think queued…';

  return (
    <Button
      type="button"
      icon={<Sparkles className={thinking ? 'animate-pulse' : undefined} />}
      disabled={thinking}
      aria-busy={thinking}
      onClick={async () => {
        try {
          setTicket(await start.mutateAsync());
        } catch (err) {
          // 429: a cycle of this actor is already queued or running; 403: not its creator.
          toast.error(describeError(err, 'Could not start a think cycle'));
        }
      }}
    >
      {label}
    </Button>
  );
}

function ArchiveButton({ actorId, actorName }: { actorId: number; actorName: string }) {
  const navigate = useNavigate();
  const archive = useArchiveAiActor(actorId);
  const [confirming, setConfirming] = useState(false);

  return (
    <>
      <Button type="button" icon={<Archive />} onClick={() => setConfirming(true)}>
        Archive
      </Button>
      <ConfirmDialog
        open={confirming}
        title="Archive AI actor"
        message={
          <>
            Archive <strong>{actorName}</strong>? It stops thinking and leaves the list (it can be shown with{' '}
            <em>Include archived</em>), but its queries and subscriptions stay.
          </>
        }
        confirmLabel="Archive"
        destructive
        busy={archive.isPending}
        onCancel={() => setConfirming(false)}
        onConfirm={() =>
          archive.mutate(undefined, {
            onSuccess: () => {
              setConfirming(false);
              navigate('/ai-actors');
            },
            onError: () => setConfirming(false),
          })
        }
      />
    </>
  );
}

// A think task that did not finish says why by its code; the actor's execution list has the cycle's own reason.
export function describeThinkOutcome(status: BackgroundTaskStatus): string {
  switch (status.failureCode) {
    case 'cooldown':
      return 'This actor thought recently; think cycles run at most once per cooldown.';
    case 'plan_pending':
      return 'A plan of this actor is waiting for a decision, so it proposes nothing new.';
    case 'forbidden':
      return 'You can no longer run this actor or work with its data source, so it did not think.';
    case 'not_found':
      return 'This AI actor is no longer available.';
    case 'abandoned':
      return 'The think cycle stopped without finishing; try again.';
    case 'cancelled':
      return 'The think cycle was cancelled.';
    case 'busy':
      return 'The LLM is busy; try again shortly.';
    default:
      return 'The think cycle failed; the server log has the details.';
  }
}
