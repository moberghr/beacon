import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { AlertTriangle, BookOpen, Bot, Layers } from 'lucide-react';
import { beaconApi } from '@/api/client';
import { Button, Card, CardBody, Field, PageHeader, Textarea } from '@/components/beacon';
import { EmptyState } from '@/components/data/EmptyState';
import { describeError, unwrap } from '@/lib/api';
import { formatDateTime } from '@/lib/format';
import { useAiActorDetailsQuery, ACTOR_STATUS_LABEL } from './queries';

// Local strict result shapes bridged via unwrap<T>() — only the fields this
// page consumes. See src/lib/api.ts.
interface RefineAiActorResult {
  success: boolean;
  errorMessage: string | null;
}

interface ActorStateChangeResult {
  success: boolean;
  actorId: number;
}

const STATUS = {
  Draft: 0,
  Active: 1,
  Paused: 2,
  Disabled: 3,
  Archived: 4,
} as const;

function useRefineActor() {
  const qc = useQueryClient();
  return useMutation<RefineAiActorResult, unknown, { id: number; feedback: string }>({
    mutationFn: async ({ id, feedback }) =>
      unwrap<RefineAiActorResult>(await beaconApi().refineAiActor(id, { feedback })),
    onSuccess: (_data, vars) => {
      qc.invalidateQueries({ queryKey: ['ai-actor', vars.id] });
    },
  });
}

function usePauseActor() {
  const qc = useQueryClient();
  return useMutation<ActorStateChangeResult, unknown, number>({
    mutationFn: async id => unwrap<ActorStateChangeResult>(await beaconApi().pauseAiActor(id)),
    onSuccess: (_data, id) => qc.invalidateQueries({ queryKey: ['ai-actor', id] }),
  });
}

function useResumeActor() {
  const qc = useQueryClient();
  return useMutation<ActorStateChangeResult, unknown, number>({
    mutationFn: async id => unwrap<ActorStateChangeResult>(await beaconApi().resumeAiActor(id)),
    onSuccess: (_data, id) => qc.invalidateQueries({ queryKey: ['ai-actor', id] }),
  });
}

export default function AiActorEditPage() {
  const { id } = useParams();
  const numericId = id ? Number.parseInt(id, 10) : Number.NaN;

  const detail = useAiActorDetailsQuery(Number.isFinite(numericId) ? numericId : undefined);
  const refine = useRefineActor();
  const pause = usePauseActor();
  const resume = useResumeActor();
  const [feedback, setFeedback] = useState('');

  useEffect(() => { setFeedback(''); }, [numericId]);

  if (Number.isNaN(numericId)) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <PageHeader eyebrow="AI actor" prefix="Edit" emphasis="actor" />
        <EmptyState icon={<AlertTriangle />} title="Invalid actor id" description={String(id)} />
      </div>
    );
  }

  const data = detail.data;
  const status = data?.status ?? 0;
  const isPaused = status === STATUS.Paused;
  const isActive = status === STATUS.Active;

  const handleRefine = async () => {
    const trimmed = feedback.trim();
    if (trimmed.length === 0) {
      toast.error('Provide feedback before refining.');
      return;
    }
    try {
      const result = await refine.mutateAsync({ id: numericId, feedback: trimmed });
      if (result.success) {
        toast.success('Actor refined');
        setFeedback('');
      } else {
        toast.error(result.errorMessage ?? 'Refinement failed');
      }
    } catch (err) {
      toast.error(describeError(err, 'Request failed'));
    }
  };

  const handlePauseResume = async () => {
    try {
      if (isPaused) {
        await resume.mutateAsync(numericId);
        toast.success('Actor resumed');
      } else {
        await pause.mutateAsync(numericId);
        toast.success('Actor paused');
      }
    } catch (err) {
      toast.error(describeError(err, 'Request failed'));
    }
  };

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        eyebrow="AI actor"
        prefix="Edit"
        emphasis={data?.name ?? 'actor'}
        sub={
          <span className="text-text-muted">
            <Link to="/ai-actors" className="text-text-muted">AI actors</Link>
            <span className="mx-1.5">/</span>
            <Link to={`/ai-actors/${numericId}`} className="text-text-muted">#{numericId}</Link>
            <span className="mx-1.5">/</span>
            Edit
          </span>
        }
        actions={
          <Link to={`/ai-actors/${numericId}`}>
            <Button type="button">View</Button>
          </Link>
        }
      />

      {detail.isLoading && (
        <Card><CardBody><span className="text-text-muted">Loading actor…</span></CardBody></Card>
      )}

      {detail.isError && (
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load actor"
          description={detail.error instanceof Error ? detail.error.message : 'Unknown error'}
        />
      )}

      {data && (
        <>
          <Card>
            <CardBody>
              <h3 className="m-0 mb-3 text-sm font-semibold text-text flex items-center gap-1.5">
                <Bot size={14} /> Status
              </h3>
              <div className="flex items-center gap-4">
                <div>
                  <div className="text-text-muted text-xs">Current state</div>
                  <div className="font-semibold text-base">
                    {ACTOR_STATUS_LABEL[status] ?? `status ${status}`}
                  </div>
                </div>
                <div className="flex-1" />
                <Button
                  type="button"
                  variant={isPaused ? 'primary' : 'secondary'}
                  onClick={handlePauseResume}
                  disabled={pause.isPending || resume.isPending || (!isPaused && !isActive)}
                  title={!isPaused && !isActive ? 'Actor must be active or paused' : undefined}
                >
                  {isPaused
                    ? (resume.isPending ? 'Resuming…' : 'Resume actor')
                    : (pause.isPending ? 'Pausing…' : 'Pause actor')}
                </Button>
              </div>
            </CardBody>
          </Card>

          <Card>
            <CardBody>
              <h3 className="m-0 mb-1 text-sm font-semibold text-text flex items-center gap-1.5">
                <Layers size={14} /> Refine actor
              </h3>
              <div className="text-text-muted text-xs mb-3">
                Provide natural-language feedback to refine the actor's instructions. The actor's LLM provider rewrites the prompt based on your input.
              </div>

              <Field label={<>Refinement feedback <span className="text-crit">*</span></>}>
                <Textarea
                  id="ae-feedback"
                  rows={6}
                  placeholder="e.g. Focus only on EU customers; ignore historical orders older than 12 months; never join against PII tables."
                  value={feedback}
                  onChange={e => setFeedback(e.target.value)}
                />
              </Field>

              <div className="flex justify-end gap-2.5 mt-3">
                <Link to={`/ai-actors/${numericId}`}>
                  <Button type="button">Cancel</Button>
                </Link>
                <Button
                  type="button"
                  variant="primary"
                  onClick={handleRefine}
                  disabled={refine.isPending || feedback.trim().length === 0}
                >
                  {refine.isPending ? 'Refining…' : 'Refine actor'}
                </Button>
              </div>
            </CardBody>
          </Card>

          <Card>
            <CardBody>
              <h3 className="m-0 mb-3 text-sm font-semibold text-text flex items-center gap-1.5">
                <BookOpen size={14} /> Current instructions
              </h3>
              <pre className="bg-surface-2 border border-border rounded-sm p-3 text-xs m-0 whitespace-pre-wrap max-h-80 overflow-auto">
                {data.instructions ?? '—'}
              </pre>
              {data.lastThinkTime && (
                <div className="text-text-muted text-xs mt-2">
                  Last think cycle: <span className="mono">{formatDateTime(data.lastThinkTime)}</span>
                </div>
              )}
            </CardBody>
          </Card>
        </>
      )}
    </div>
  );
}
