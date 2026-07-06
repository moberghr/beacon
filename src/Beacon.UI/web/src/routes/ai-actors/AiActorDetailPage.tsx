import { Link, useParams } from 'react-router-dom';
import { AlertTriangle, ArrowLeftRight } from 'lucide-react';
import { Button, Card, CardBody, KPI, KPIGrid, PageHeader } from '@/components/beacon';
import { EmptyState } from '@/components/data/EmptyState';
import { formatDateTime, formatNumber } from '@/lib/format';
import { useAiActorDetailsQuery, ACTOR_STATUS_LABEL } from './queries';

export default function AiActorDetailPage() {
  const params = useParams<{ id: string }>();
  const id = params.id ? Number(params.id) : undefined;
  const { data, isLoading, isError, error } = useAiActorDetailsQuery(id);

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
          <Link to="/ai-actors">
            <Button type="button" icon={<ArrowLeftRight />}>
              All actors
            </Button>
          </Link>
        }
      />

      {isError && (
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load actor"
          description={error instanceof Error ? error.message : 'Unknown error'}
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
              </dl>
            </CardBody>
          </Card>
        </>
      )}
    </div>
  );
}
