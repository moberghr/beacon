import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { AlertTriangle, ArrowLeftRight } from 'lucide-react';
import { Button, Card, CardBody, KPI, KPIGrid, PageHeader, Pill } from '@/components/beacon';
import { EmptyState } from '@/components/data/EmptyState';
import { isNotFound, NOT_FOUND_DETAIL } from '@/lib/api';
import { formatDateTime, formatNumber } from '@/lib/format';
import { useIsAdmin } from '@/auth/useAuth';
import { SetOwnerDialog } from '../users/SetOwnerDialog';
import {
  useAiActorDetailsQuery,
  useSetAiActorOwner,
  ACTOR_PHASE_LABEL,
  ACTOR_STATUS_LABEL,
  type AiActorDetails,
} from './queries';

/** The creator by name, never by id: a neutral label when none is recorded or the user no longer exists. */
function ownerLabel(actor: Pick<AiActorDetails, 'hasOwner' | 'ownerDisplayName'>): string {
  if (!actor.hasOwner) {
    return 'No owner recorded';
  }
  return actor.ownerDisplayName ?? 'Unknown user';
}

export default function AiActorDetailPage() {
  const params = useParams<{ id: string }>();
  const id = params.id ? Number(params.id) : undefined;
  const { data, isLoading, isError, error } = useAiActorDetailsQuery(id);
  const setOwner = useSetAiActorOwner(id ?? 0);
  const isAdmin = useIsAdmin() === true;
  const [changingOwner, setChangingOwner] = useState(false);

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        eyebrow="AI actor"
        emphasis={data?.name ?? (isLoading ? 'Loading…' : 'AI actor')}
        sub={
          data
            ? <span className="text-text-muted">on <strong>{data.dataSourceName}</strong> · {ACTOR_STATUS_LABEL[data.status ?? 0]}</span>
            : null
        }
        actions={
          <>
            {isAdmin && data && (
              <Button type="button" onClick={() => setChangingOwner(true)}>
                Change owner
              </Button>
            )}
            <Link to="/ai-actors">
              <Button type="button" icon={<ArrowLeftRight />}>
                All actors
              </Button>
            </Link>
          </>
        }
      />

      {isAdmin && changingOwner && (
        <SetOwnerDialog
          open
          noun="AI actor"
          busy={setOwner.isPending}
          onClose={() => setChangingOwner(false)}
          onSubmit={userId => setOwner.mutate(userId, { onSuccess: () => setChangingOwner(false) })}
        />
      )}

      {isError && (
        <EmptyState
          icon={<AlertTriangle />}
          title={isNotFound(error) ? 'AI actor not found' : 'Failed to load actor'}
          description={isNotFound(error) ? NOT_FOUND_DETAIL : error instanceof Error ? error.message : 'Unknown error'}
        />
      )}

      {data && (
        <>
          <KPIGrid>
            <KPI label="Think cycles" value={formatNumber(data.thinkCount ?? 0)} />
            <KPI label="Tokens used" value={formatNumber(data.totalTokensUsed ?? 0)} />
            <KPI label="Total cost" value={`$${(data.totalCost ?? 0).toFixed(4)}`} />
            <KPI label="Max queries" value={formatNumber(data.maxQueries ?? 0)} />
            <KPI
              label="Last think"
              value={data.lastThinkTime ? formatDateTime(data.lastThinkTime) : '—'}
            />
          </KPIGrid>

          <Card>
            <CardBody>
              <h2 className="m-0 mb-2 text-base font-semibold text-text">Instructions</h2>
              <pre className="whitespace-pre-wrap m-0 font-sans text-sm">
                {data.instructions ?? '—'}
              </pre>
            </CardBody>
          </Card>

          {data.additionalContext && (
            <Card>
              <CardBody>
                <h2 className="m-0 mb-2 text-base font-semibold text-text">Additional context</h2>
                <pre className="whitespace-pre-wrap m-0 font-sans text-sm">
                  {data.additionalContext}
                </pre>
              </CardBody>
            </Card>
          )}

          <Card>
            <CardBody>
              <h2 className="m-0 mb-2 text-base font-semibold text-text">Configuration</h2>
              <dl className="grid grid-cols-[180px_1fr] gap-x-3 gap-y-1.5 m-0 text-sm">
                <dt className="text-text-muted">Max queries</dt><dd>{data.maxQueries}</dd>
                <dt className="text-text-muted">Max subscriptions/query</dt><dd>{data.maxSubscriptionsPerQuery}</dd>
                <dt className="text-text-muted">Requires approval</dt><dd>{data.requiresApproval ? 'Yes' : 'No'}</dd>
                <dt className="text-text-muted">Status</dt><dd>{ACTOR_STATUS_LABEL[data.status ?? 0]}</dd>
                <dt className="text-text-muted">Owner</dt><dd>{ownerLabel(data)}</dd>
              </dl>
            </CardBody>
          </Card>

          <Card>
            <CardBody>
              <h2 className="m-0 mb-2 text-base font-semibold text-text">Recent think cycles</h2>
              {(data.recentExecutions ?? []).length === 0 ? (
                <p className="m-0 text-sm text-text-muted">No think cycles yet.</p>
              ) : (
                <ul className="m-0 p-0 list-none flex flex-col">
                  {data.recentExecutions.map(x => {
                    const phase = ACTOR_PHASE_LABEL[x.phase] ?? { label: String(x.phase), tone: 'neutral' as const };
                    return (
                      <li
                        key={x.executionId}
                        className="flex flex-col gap-0.5 py-2 border-b border-border last:border-b-0 text-sm"
                      >
                        <span className="flex items-center gap-2">
                          <Pill tone={phase.tone}>{phase.label}</Pill>
                          <span className="text-text-muted text-xs">{formatDateTime(x.startedAt)}</span>
                        </span>
                        {(x.errorMessage ?? x.decisionSummary) && (
                          <span className={x.errorMessage ? 'text-xs text-text-muted' : 'text-xs'}>
                            {x.errorMessage ?? x.decisionSummary}
                          </span>
                        )}
                      </li>
                    );
                  })}
                </ul>
              )}
            </CardBody>
          </Card>
        </>
      )}
    </div>
  );
}
