import { useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { AlertTriangle, ArrowLeftRight, Layers, RefreshCw } from 'lucide-react';
import { Card, CardBody, Button, PageHeader, Pill } from '@/components/beacon';
import { EmptyState } from '@/components/data/EmptyState';
import { formatDateTime } from '@/lib/format';
import type { QueryStepSnapshot } from '@/api/generated/beacon-api';
import {
  useQueryDetailQuery,
  useQueryVersionDetailQuery,
  useRestoreQueryVersion,
} from './queries';

export default function QueryVersionDetailPage() {
  const { id, versionId } = useParams();
  const navigate = useNavigate();
  const queryId = id ? Number.parseInt(id, 10) : Number.NaN;
  const versionIdNum = versionId ? Number.parseInt(versionId, 10) : Number.NaN;

  const currentQuery = useQueryDetailQuery(Number.isNaN(queryId) ? undefined : queryId);
  const versionQuery = useQueryVersionDetailQuery(Number.isNaN(versionIdNum) ? undefined : versionIdNum);
  const restoreMutation = useRestoreQueryVersion(queryId);
  const [restoreConfirm, setRestoreConfirm] = useState(false);

  if (Number.isNaN(queryId) || Number.isNaN(versionIdNum)) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <PageHeader eyebrow="Query version" emphasis="detail" />
        <EmptyState icon={<AlertTriangle size={20} />} title="Invalid id in URL" />
      </div>
    );
  }

  if (versionQuery.isError) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <PageHeader eyebrow="Query version" emphasis="detail" />
        <EmptyState
          icon={<AlertTriangle size={20} />}
          title="Failed to load version"
          description={versionQuery.error instanceof Error ? versionQuery.error.message : 'Unknown error'}
        />
      </div>
    );
  }

  const version = versionQuery.data;
  const current = currentQuery.data;

  const handleRestore = async () => {
    await restoreMutation.mutateAsync(versionIdNum);
    setRestoreConfirm(false);
    navigate(`/queries/${queryId}`);
  };

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        eyebrow="Query version"
        emphasis={`Version v${version?.versionNumber ?? versionId}`}
        sub={
          <span className="text-text-muted">
            <Link to={`/queries/${queryId}/versions`} className="text-brand-600">← versions</Link>
            <span className="mx-1.5">·</span>
            {version?.createdTime ? formatDateTime(version.createdTime) : '—'}
            {version?.changeSource && (
              <span className="ml-1.5">· {version.changeSource}</span>
            )}
            {version?.changeReason && (
              <span className="ml-1.5">· "{version.changeReason}"</span>
            )}
          </span>
        }
        actions={
          <div className="flex gap-2">
            <Link to={`/queries/${queryId}`}>
              <Button icon={<ArrowLeftRight />}>Current version</Button>
            </Link>
            {!restoreConfirm ? (
              <Button
                variant="primary"
                icon={<RefreshCw />}
                onClick={() => setRestoreConfirm(true)}
                disabled={restoreMutation.isPending}
              >
                Restore this version
              </Button>
            ) : (
              <>
                <Button onClick={() => setRestoreConfirm(false)}>Cancel</Button>
                <Button
                  variant="primary"
                  onClick={handleRestore}
                  disabled={restoreMutation.isPending}
                >
                  {restoreMutation.isPending ? 'Restoring…' : 'Confirm restore'}
                </Button>
              </>
            )}
          </div>
        }
      />

      {versionQuery.isLoading && (
        <div className="text-text-muted">Loading version…</div>
      )}

      {version && (
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
          <Card>
            <CardBody>
              <div className="font-semibold mb-2 flex items-center gap-2">
                This version <Pill>v{version.versionNumber}</Pill>
              </div>
              <div className="text-text-muted text-xs mb-2">
                {version.name} · {(version.steps ?? []).length} step{(version.steps ?? []).length === 1 ? '' : 's'}
              </div>
              {(version.steps ?? []).map(step => (
                <StepBlock key={step.stepOrder} step={step} label="version" />
              ))}
              {version.finalQuery && (
                <div className="mt-3">
                  <div className="text-text-muted text-xs mb-1">Final query</div>
                  <pre className="m-0 p-2.5 bg-surface-2 border border-border rounded-sm mono text-xs overflow-x-auto whitespace-pre-wrap">
                    {version.finalQuery}
                  </pre>
                </div>
              )}
            </CardBody>
          </Card>

          <Card>
            <CardBody>
              <div className="font-semibold mb-2 flex items-center gap-2">
                Current version <Pill tone="ok">live</Pill>
              </div>
              {currentQuery.isLoading && <div className="text-text-muted">Loading…</div>}
              {current && (
                <>
                  <div className="text-text-muted text-xs mb-2">
                    {current.name} · {current.steps.length} step{current.steps.length === 1 ? '' : 's'}
                  </div>
                  {current.steps.map(step => (
                    <StepBlock
                      key={step.stepOrder}
                      step={{ stepOrder: step.stepOrder, stepName: step.name, sqlValue: step.sqlValue }}
                      label="current"
                    />
                  ))}
                  {current.finalQuery && (
                    <div className="mt-3">
                      <div className="text-text-muted text-xs mb-1">Final query</div>
                      <pre className="m-0 p-2.5 bg-surface-2 border border-border rounded-sm mono text-xs overflow-x-auto whitespace-pre-wrap">
                        {current.finalQuery}
                      </pre>
                    </div>
                  )}
                </>
              )}
            </CardBody>
          </Card>
        </div>
      )}

      {version && (version.steps ?? []).length === 0 && !currentQuery.isLoading && (
        <EmptyState
          icon={<Layers size={20} />}
          title="No steps in this version"
          description="This version snapshot has no step data recorded."
        />
      )}
    </div>
  );
}

interface StepBlockStep {
  stepOrder?: number;
  stepName?: string | null;
  sqlValue?: string | null;
}

function StepBlock({ step }: { step: StepBlockStep | QueryStepSnapshot; label?: string }) {
  const name = (step as StepBlockStep).stepName ?? (step as QueryStepSnapshot).name ?? `Step ${step.stepOrder}`;
  const sql = (step as StepBlockStep).sqlValue ?? (step as QueryStepSnapshot).sqlValue ?? '';

  return (
    <div className="mb-2.5">
      <div className="flex items-center gap-2 mb-1">
        <span className="text-text-muted text-2xs">Step {step.stepOrder}</span>
        <span className="text-xs font-medium">{name}</span>
      </div>
      <pre className="m-0 p-2.5 bg-surface-2 border border-border rounded-sm mono text-xs overflow-x-auto max-h-[200px] whitespace-pre-wrap">
        {sql || <span className="text-text-muted">(empty)</span>}
      </pre>
    </div>
  );
}
