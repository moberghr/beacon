import { Link, useParams } from 'react-router-dom';
import { Icon } from '@/components/Icon';
import { PageHeader } from '@/components/layout/PageHeader';
import { EmptyState } from '@/components/data/EmptyState';
import { formatDateTime, formatNumber } from '@/lib/format';
import { useAiActorDetailsQuery, ACTOR_STATUS_LABEL } from './queries';

export default function AiActorDetailPage() {
  const params = useParams<{ id: string }>();
  const id = params.id ? Number(params.id) : undefined;
  const { data, isLoading, isError, error } = useAiActorDetailsQuery(id);

  return (
    <div className="page">
      <PageHeader
        title={data?.name ?? (isLoading ? 'Loading…' : 'AI actor')}
        sub={
          data
            ? <span className="muted">on <strong>{data.dataSourceName}</strong> · {ACTOR_STATUS_LABEL[data.status ?? 0]}</span>
            : null
        }
        actions={
          <Link className="btn" to="/ai-actors">
            <Icon.ArrowsLR size={14} className="btn__icon" />
            All actors
          </Link>
        }
      />

      {isError && (
        <EmptyState
          icon={<Icon.Alert size={20} />}
          title="Failed to load actor"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      )}

      {data && (
        <>
          <div
            style={{
              display: 'grid',
              gridTemplateColumns: 'repeat(auto-fill, minmax(180px, 1fr))',
              gap: 12,
              marginBottom: 16,
            }}
          >
            <KpiCard label="Think cycles" value={formatNumber(data.thinkCount ?? 0)} />
            <KpiCard label="Tokens used" value={formatNumber(data.totalTokensUsed ?? 0)} />
            <KpiCard label="Total cost" value={`$${(data.totalCost ?? 0).toFixed(4)}`} />
            <KpiCard label="Max queries" value={formatNumber(data.maxQueries ?? 0)} />
            <KpiCard
              label="Last think"
              value={data.lastThinkTime ? formatDateTime(data.lastThinkTime) : '—'}
            />
          </div>

          <div className="card" style={{ padding: 16, marginBottom: 16 }}>
            <h2 style={{ marginTop: 0, fontSize: 16 }}>Instructions</h2>
            <pre style={{ whiteSpace: 'pre-wrap', margin: 0, fontFamily: 'inherit' }}>
              {data.instructions ?? '—'}
            </pre>
          </div>

          {data.additionalContext && (
            <div className="card" style={{ padding: 16, marginBottom: 16 }}>
              <h2 style={{ marginTop: 0, fontSize: 16 }}>Additional context</h2>
              <pre style={{ whiteSpace: 'pre-wrap', margin: 0, fontFamily: 'inherit' }}>
                {data.additionalContext}
              </pre>
            </div>
          )}

          <div className="card" style={{ padding: 16 }}>
            <h2 style={{ marginTop: 0, fontSize: 16 }}>Configuration</h2>
            <dl style={{ display: 'grid', gridTemplateColumns: '180px 1fr', rowGap: 6, columnGap: 12, margin: 0 }}>
              <dt className="muted">Max queries</dt><dd>{data.maxQueries}</dd>
              <dt className="muted">Max subscriptions/query</dt><dd>{data.maxSubscriptionsPerQuery}</dd>
              <dt className="muted">Requires approval</dt><dd>{data.requiresApproval ? 'Yes' : 'No'}</dd>
              <dt className="muted">Status</dt><dd>{ACTOR_STATUS_LABEL[data.status ?? 0]}</dd>
            </dl>
          </div>

          <div className="muted" style={{ marginTop: 16, fontSize: 12 }}>
            Refine, plan review, and execution-history detail tabs are migrating in a follow-up batch.
          </div>
        </>
      )}
    </div>
  );
}

function KpiCard({ label, value }: { label: string; value: string }) {
  return (
    <div className="card" style={{ padding: 12 }}>
      <div className="muted" style={{ fontSize: 11, textTransform: 'uppercase', letterSpacing: 0.5 }}>{label}</div>
      <div style={{ fontSize: 22, fontWeight: 700, color: 'var(--text)', marginTop: 4 }}>{value}</div>
    </div>
  );
}
