import { useCallback, useEffect, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { AlertTriangle } from 'lucide-react';
import { EmptyState } from '@/components/data/EmptyState';
import { useAuth } from '@/auth/useAuth';
import { formatRelativeTime } from '@/lib/format';
import {
  useAssignTask,
  useSnoozeTask,
  useTaskCommentsQuery,
  useTaskDetailQuery,
  useTaskExecutionsQuery,
  useTaskRelatedQuery,
  useTaskResultHistoryQuery,
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
const COMMENT_TEXTAREA_ID = 'investigation-log-textarea';

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

  const { data: currentUser } = useAuth();
  // Hooks must be called unconditionally — initialize even when validId is unavailable.
  const assign = useAssignTask(validId ?? 0);
  const snooze = useSnoozeTask(validId ?? 0);

  const onResolve = useCallback(() => setResolveOpen(true), []);

  const task = detail.data;

  // Keyboard shortcuts: R resolve, A assign-to-me, S snooze 1h, C focus comment.
  // Ignored when typing in inputs/textareas/contenteditable.
  useEffect(() => {
    if (!task) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.repeat) return;
      if (e.metaKey || e.ctrlKey || e.altKey) return;
      const target = e.target as HTMLElement | null;
      const tag = target?.tagName?.toLowerCase();
      if (tag === 'input' || tag === 'textarea' || tag === 'select') return;
      if (target?.isContentEditable) return;

      const k = e.key.toLowerCase();
      if (k === 'r' && !task.resolved) {
        e.preventDefault();
        setResolveOpen(true);
      } else if (k === 'a' && !task.resolved && currentUser?.userId) {
        e.preventDefault();
        if (!assign.isPending && task.assigneeUserId !== currentUser.userId) {
          assign.mutate({ assigneeUserId: currentUser.userId });
        }
      } else if (k === 's' && !task.resolved) {
        e.preventDefault();
        if (!snooze.isPending) {
          snooze.mutate({
            snoozeUntil: new Date(Date.now() + 3_600_000).toISOString(),
          });
        }
      } else if (k === 'c') {
        e.preventDefault();
        const ta = document.getElementById(COMMENT_TEXTAREA_ID) as HTMLTextAreaElement | null;
        ta?.focus();
      }
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [task, currentUser?.userId, assign, snooze]);

  if (!Number.isFinite(id)) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <EmptyState icon={<AlertTriangle />} title="Invalid task id" />
      </div>
    );
  }

  if (detail.isError) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load task"
          description={detail.error instanceof Error ? detail.error.message : 'Unknown error'}
        />
      </div>
    );
  }

  if (detail.isLoading || !task) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <div className="text-text-muted">Loading task…</div>
      </div>
    );
  }

  const ageMs = Date.now() - new Date(task.createdAt).getTime();
  const ageHours = Math.max(0, ageMs / 3_600_000);
  const ageLabel = formatAge(ageMs);
  const ageDetail = `since ${formatRelativeTime(task.createdAt)}`;
  const slaHours = task.slaHours ?? SLA_HOURS_DEFAULT;
  const slaRemainingMs = slaHours * 3_600_000 - ageMs;
  const slaRemainingLabel = !task.resolved && slaRemainingMs > 0 ? formatAge(slaRemainingMs) : null;

  const relatedResolvedCount = related.data?.related.filter(x => x.resolved).length ?? 0;

  return (
    <div className="flex flex-col gap-5 p-7" data-screen-label="04 Task Detail">
      <TaskHero
        task={task}
        ageLabel={ageLabel}
        slaRemainingLabel={slaRemainingLabel}
        onResolve={onResolve}
      />

      <SlaBanner
        resolved={task.resolved}
        ageHours={ageHours}
        slaHours={slaHours}
        resolvedByName={task.resolvedByUserName}
        resolvedRelative={task.resolvedAt ? formatRelativeTime(task.resolvedAt) : null}
        notificationCount={task.notificationCount}
        taskId={task.id}
        subscriptionId={task.subscriptionId}
        customSla={task.slaHours != null}
      />

      <TaskKpiGrid
        latestResultCount={task.latestResultCount}
        executionCount={executions.data?.executions.length ?? 0}
        notificationCount={task.notificationCount}
        ageLabel={ageLabel}
        ageDetail={ageDetail}
      />

      <div className="grid gap-5 lg:grid-cols-[minmax(0,1fr)_320px] items-start">
        <div className="flex flex-col gap-5 min-w-0">
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
          <InvestigationLogCard taskId={task.id} textareaId={COMMENT_TEXTAREA_ID} />
        </div>

        <RightRail task={task} relatedResolvedCount={relatedResolvedCount} />
      </div>

      <TaskSaveBar
        task={task}
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
