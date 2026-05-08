import { Icon } from '@/components/Icon';
import { useAuth } from '@/auth/useAuth';
import { useToggleQueryLock, type QueryDetail } from '../queries';

interface QuerySaveBarProps {
  query: QueryDetail;
  legacyEditHref: string;
  legacyExecuteHref: string;
}

export function QuerySaveBar({ query, legacyEditHref, legacyExecuteHref }: QuerySaveBarProps) {
  const { data: currentUser } = useAuth();
  const toggleLock = useToggleQueryLock(query.id);

  const onToggleLock = () => {
    if (toggleLock.isPending) return;
    toggleLock.mutate({ lock: !query.isLocked, userId: currentUser?.userId ?? null });
  };

  return (
    <div className="save-bar">
      <span className="save-bar__hint">
        {query.isLocked
          ? <span className="pill pill--warn"><Icon.Lock size={10} /> LOCKED</span>
          : <span className="pill pill--ok"><span className="pill__dot" />UNLOCKED</span>}
        <span>
          <span className="mono">#{query.id}</span> · {query.totalExecutions.toLocaleString()} run{query.totalExecutions === 1 ? '' : 's'}
          {' · '}{query.subscriptions.length} subscription{query.subscriptions.length === 1 ? '' : 's'}
        </span>
      </span>
      <div className="spacer" />
      <span className="save-bar__hint">
        <span>Press</span>
        <span className="kbd">⌘</span><span className="kbd">↵</span>
        <span>to execute</span>
      </span>
      <button
        type="button"
        className="btn"
        onClick={onToggleLock}
        disabled={toggleLock.isPending}
      >
        <Icon.Lock size={14} className="btn__icon" />
        {query.isLocked ? ' Unlock' : ' Lock'}
      </button>
      <a className="btn" href={legacyEditHref}>
        <Icon.Sliders size={14} className="btn__icon" /> Edit SQL
      </a>
      <a className="btn btn--primary" href={legacyExecuteHref}>
        <Icon.Bolt size={14} className="btn__icon" /> Execute query
      </a>
    </div>
  );
}
