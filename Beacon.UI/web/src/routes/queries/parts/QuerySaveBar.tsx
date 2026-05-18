import { Link } from 'react-router-dom';
import { Lock, SlidersHorizontal, Zap } from 'lucide-react';
import { Button, Pill, Kbd } from '@/components/beacon';
import { useAuth } from '@/auth/useAuth';
import { useToggleQueryLock, type QueryDetail } from '../queries';

interface QuerySaveBarProps {
  query: QueryDetail;
  editHref: string;
  onExecute: () => void;
  executePending?: boolean;
}

export function QuerySaveBar({ query, editHref, onExecute, executePending }: QuerySaveBarProps) {
  const { data: currentUser } = useAuth();
  const toggleLock = useToggleQueryLock(query.id);

  const onToggleLock = () => {
    if (toggleLock.isPending) return;
    toggleLock.mutate({ lock: !query.isLocked, userId: currentUser?.userId ?? null });
  };

  return (
    <div className="flex items-center gap-2 px-5 py-3 border-t border-border bg-surface-2 rounded-md">
      <div className="text-2xs text-text-muted flex items-center gap-1.5">
        {query.isLocked
          ? <Pill tone="warn"><Lock size={10} /> LOCKED</Pill>
          : <Pill tone="ok" dot>UNLOCKED</Pill>}
        <span>
          <span className="mono">#{query.id}</span> · {query.totalExecutions.toLocaleString()} run{query.totalExecutions === 1 ? '' : 's'}
          {' · '}{query.subscriptions.length} subscription{query.subscriptions.length === 1 ? '' : 's'}
        </span>
      </div>
      <div className="ml-auto flex items-center gap-1.5">
        <span className="text-2xs text-text-muted flex items-center gap-1.5 mr-2">
          <span>Press</span>
          <Kbd>⌘</Kbd><Kbd>↵</Kbd>
          <span>to execute</span>
        </span>
        <Button icon={<Lock />} onClick={onToggleLock} disabled={toggleLock.isPending}>
          {query.isLocked ? 'Unlock' : 'Lock'}
        </Button>
        <Link to={editHref}>
          <Button icon={<SlidersHorizontal />}>Edit SQL</Button>
        </Link>
        <Button
          variant="primary"
          icon={<Zap />}
          onClick={onExecute}
          disabled={executePending}
        >
          {executePending ? 'Running…' : 'Execute query'}
        </Button>
      </div>
    </div>
  );
}
