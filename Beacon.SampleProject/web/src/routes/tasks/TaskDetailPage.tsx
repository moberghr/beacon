import { useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { Icon } from '@/components/Icon';
import { EmptyState } from '@/components/data/EmptyState';
import { formatRelativeTime } from '@/lib/format';
import {
  useTaskDetailQuery,
  useTaskExecutionsQuery,
  useTaskRelatedQuery,
  useTaskResultHistoryQuery,
  useTaskCommentsQuery,
} from './queries';
import { ResolveTaskDialog } from './ResolveTaskDialog';
import { TaskHero } from './parts/TaskHero';
import { SlaBanner } from './parts/SlaBanner';
import { TaskKpiGrid } from './parts/TaskKpiGrid';
import { TaskInfoCard } from './parts/TaskInfoCard';
import { TaskResultChart } from './parts/TaskResultChart';
import { TaskTabsCard, type TabKey } from './parts/TaskTabsCard';
import { InvestigationLogCard } from './parts/InvestigationLogCard';
import { RightRail } from './parts/RightRail';
import { TaskSaveBar } from './parts/TaskSaveBar';

const SLA_HOURS_DEFAULT = 24;

export default function TaskDetailPage() {
  const params = useParams<{ id: string }>();
  const id = Number(params.id);
  const navigate = useNavigate();
  const [resolveOpen, setResolveOpen] = useState(false);
  const [tab, setTab] = useState<TabKey>('activity');

  const validId = Number.isFinite(id) ? id : undefined;
  const detail = useTaskDetailQuery(validId);
  const executions = useTaskExecutionsQuery(validId);
  const related = useTaskRelatedQuery(validId);
  const history = useTaskResultHistoryQuery(validId);
  const comments = useTaskCommentsQuery(validId);

  if (!Number.isFinite(id)) {
    return (
      <div className="page">
        <EmptyState icon={<Icon.Alert size={20} />} title="Invalid task id" />
      </div>
    );
  }

  if (detail.isError) {
    return (
      <div className="page">
        <EmptyState
          icon={<Icon.Alert size={20} />}
          title="Failed to load task"
          description={detail.error instanceof Error ? detail.error.message : 'Unknown error'}
        />
      </div>
    );
  }

  if (detail.isLoading || !detail.data) {
    return (
      <div className="page">
        <div className="muted">Loading task…</div>
      </div>
    );
  }

  const task = detail.data;
  const ageMs = Date.now() - new Date(task.createdAt).getTime();
  const ageHours = Math.max(0, ageMs / 3_600_000);
  const ageLabel = formatAge(ageMs);
  const ageDetail = `since ${formatRelativeTime(task.createdAt)}`;
  const slaRemainingMs = SLA_HOURS_DEFAULT * 3_600_000 - ageMs;
  const slaRemainingLabel = !task.resolved && slaRemainingMs > 0 ? formatAge(slaRemainingMs) : null;

  const onResolve = () => setResolveOpen(true);

  const relatedResolvedCount = related.data?.related.filter(x => x.resolved).length ?? 0;

  return (
    <div className="page" data-screen-label="04 Task Detail">
      <TaskHero
        taskId={task.id}
        resolved={task.resolved}
        subscriptionName={task.subscriptionName}
        ageLabel={ageLabel}
        slaRemainingLabel={slaRemainingLabel}
        onResolve={onResolve}
      />

      <SlaBanner
        resolved={task.resolved}
        ageHours={ageHours}
        slaHours={SLA_HOURS_DEFAULT}
        resolvedByName={task.resolvedByUserName}
        resolvedRelative={task.resolvedAt ? formatRelativeTime(task.resolvedAt) : null}
        notificationCount={task.notificationCount}
      />

      <TaskKpiGrid
        latestResultCount={task.latestResultCount}
        executionCount={executions.data?.executions.length ?? 0}
        notificationCount={task.notificationCount}
        ageLabel={ageLabel}
        ageDetail={ageDetail}
      />

      <div className="q-layout">
        <div className="q-section">
          <TaskInfoCard task={task} />
          <TaskResultChart points={history.data?.points ?? []} />
          <TaskTabsCard
            tab={tab}
            onTabChange={setTab}
            task={task}
            executions={executions.data?.executions ?? []}
            comments={comments.data?.comments ?? []}
            related={related.data?.related ?? []}
            onRefresh={() => {
              detail.refetch();
              executions.refetch();
              related.refetch();
              history.refetch();
            }}
          />
          <InvestigationLogCard taskId={task.id} />
        </div>

        <RightRail task={task} relatedResolvedCount={relatedResolvedCount} />
      </div>

      <TaskSaveBar
        resolved={task.resolved}
        ageLabel={ageLabel}
        slaRemainingLabel={slaRemainingLabel}
        onResolve={onResolve}
      />

      <ResolveTaskDialog
        open={resolveOpen}
        taskId={task.id}
        onClose={() => {
          setResolveOpen(false);
          navigate(`/tasks/${task.id}`, { replace: true });
        }}
      />
    </div>
  );
}

function formatAge(ms: number): string {
  if (ms <= 0) return '0m';
  const totalMin = Math.floor(ms / 60000);
  if (totalMin < 60) return `${totalMin}m`;
  const hr = Math.floor(totalMin / 60);
  const min = totalMin % 60;
  if (hr < 24) return min === 0 ? `${hr}h` : `${hr}h ${min}m`;
  const days = Math.floor(hr / 24);
  const remHr = hr % 24;
  return remHr === 0 ? `${days}d` : `${days}d ${remHr}h`;
}
